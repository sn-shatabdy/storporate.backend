using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Llm;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Persistence.TalentIndex;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.TalentIndex;

/// <summary>
/// Live, pgvector-backed proof for <see cref="TalentIndexRepository"/>'s production
/// path. Gated on <c>STORPORATE_TEST_PG</c> set to a non-empty Npgsql connection string;
/// every test is reported as <c>Skipped</c> (not <c>Failed</c>, not silently
/// <c>Passed</c>) when the variable is unset, so CI hosts without a reachable
/// Postgres stay green and the full assertion path only runs against the
/// throwaway <c>storporate-pgvector-test</c> container this story ships.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a custom <see cref="LivePgFactAttribute"/> instead of an early-return.</b>
/// The repo's existing <c>AuditLogWriterLiveChainTests</c> uses an early-return so the
/// test is reported as <c>Passed</c> when the env var is missing. That's the wrong
/// shape for this file — a silent pass masks accidental CI dead-code, while a
/// <c>Skipped</c> report makes the test's gate explicit at the runner level. xUnit
/// 2.9.3 honours <see cref="FactAttribute.Skip"/>; the custom attribute below sets it
/// dynamically so the test reports as <c>Skipped</c> with a self-describing reason
/// when <c>STORPORATE_TEST_PG</c> is missing or empty.
/// </para>
/// <para>
/// <b>Why these specific cases.</b> They cover every production-only behaviour the
/// unit suite can't reach: the raw Npgsql upsert with an <c>@embedding::vector</c>
/// cast, the search query with a parameter of type <c>Unknown</c> and no explicit
/// cast (the case the story flagging this file called out), and the delete's
/// idempotent no-op on an unknown student. The brute-force InMemory branch is
/// already covered exhaustively by <c>TalentIndexRepositoryTests</c>; this file
/// proves the two branches produce the same ordering on the same data, which is
/// the cross-validation the upstream story asks for.
/// </para>
/// <para>
/// <b>Per-test isolation.</b> Every test uses <see cref="Guid.NewGuid"/> for the
/// <see cref="TalentIndexEntry.StudentAccountId"/> and removes its row(s) in a
/// <c>try/finally</c>, so a parallel test run, a hand-driven psql session, or a
/// crashed prior test can never leave state behind that would distort the next
/// run's assertion.
/// </para>
/// </remarks>
public class TalentIndexRepositoryPostgresTests
{
    private const string LivePgEnvVar = "STORPORATE_TEST_PG";

    [LivePgFact]
    public async Task UpsertAsync_SameStudentTwice_OverwritesAllFieldsAndKeepsOneRow()
    {
        var connectionString = RequireConnectionString();
        var repository = CreateRepository(connectionString);
        var studentAccountId = Guid.NewGuid();

        try
        {
            var first = MakeUnitVector(slot: 0);
            await repository.UpsertAsync(
                studentAccountId,
                displayName: "Original",
                headline: null,
                university: null,
                fieldOfStudy: null,
                studyYear: null,
                itemsJson: "[]",
                searchText: "original",
                contentHash: "hash-original",
                embedding: first,
                updatedAt: DateTimeOffset.UtcNow,
                cancellationToken: CancellationToken.None);

            var second = MakeUnitVector(slot: 0);
            await repository.UpsertAsync(
                studentAccountId,
                displayName: "Updated",
                headline: "New headline",
                university: "MIT",
                fieldOfStudy: "Computer Science",
                studyYear: 3,
                itemsJson: "[{\"label\":\"Item\"}]",
                searchText: "updated",
                contentHash: "hash-updated",
                embedding: second,
                updatedAt: DateTimeOffset.UtcNow,
                cancellationToken: CancellationToken.None);

            // Re-read through a fresh WriteDbContext so the assertion queries
            // through EF, not through the repository's own connection.
            await using var dbContext = await CreateDbContextAsync(connectionString);
            var rows = await dbContext.TalentIndexEntries
                .AsNoTracking()
                .Where(e => e.StudentAccountId == studentAccountId)
                .ToListAsync();

            var row = Assert.Single(rows);
            Assert.Equal("Updated", row.DisplayName);
            Assert.Equal("New headline", row.Headline);
            Assert.Equal("MIT", row.University);
            Assert.Equal("Computer Science", row.FieldOfStudy);
            Assert.Equal(3, row.StudyYear);
            Assert.Equal("hash-updated", row.ContentHash);

            // The vector column is intentionally outside the EF model; read it
            // through a raw Npgsql connection so the assertion sees the actual
            // pgvector value, not a missing scalar.
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "SELECT \"Embedding\"::text FROM \"TalentIndexEntries\" WHERE \"StudentAccountId\" = @id",
                connection);
            cmd.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = studentAccountId });
            var vectorLiteral = (string)(await cmd.ExecuteScalarAsync())!;
            Assert.Equal(FormatVectorLiteralForCompare(second), vectorLiteral);
        }
        finally
        {
            await DeleteQuietAsync(repository, studentAccountId);
        }
    }

    [LivePgFact]
    public async Task SearchNearestAsync_AxisZeroQuery_ReturnsAxisZeroStudentFirst()
    {
        var connectionString = RequireConnectionString();
        var repository = CreateRepository(connectionString);

        // Tag students so test failures point at the right axis slot.
        var studentA0 = (Id: Guid.NewGuid(), Display: "axis-0");
        var studentA1 = (Id: Guid.NewGuid(), Display: "axis-1");
        var studentA2 = (Id: Guid.NewGuid(), Display: "axis-2");
        var studentMixed = (Id: Guid.NewGuid(), Display: "mixed");

        try
        {
            await repository.UpsertAsync(studentA0.Id, studentA0.Display, null, null, null, null, "[]", "a0", "h0", MakeUnitVector(0), DateTimeOffset.UtcNow, CancellationToken.None);
            await repository.UpsertAsync(studentA1.Id, studentA1.Display, null, null, null, null, "[]", "a1", "h1", MakeUnitVector(1), DateTimeOffset.UtcNow, CancellationToken.None);
            await repository.UpsertAsync(studentA2.Id, studentA2.Display, null, null, null, null, "[]", "a2", "h2", MakeUnitVector(2), DateTimeOffset.UtcNow, CancellationToken.None);
            // Mixed: equal non-zero on axes 1 and 2 → cosine distance 0.5 to either axis-1 or axis-2,
            // distance 1 to axis-0.
            await repository.UpsertAsync(studentMixed.Id, studentMixed.Display, null, null, null, null, "[]", "mx", "hm", MakeMixedVector(new[] { 1, 2 }), DateTimeOffset.UtcNow, CancellationToken.None);

            // Axis-0 query: studentA0 first (distance 0); the remaining three
            // (mixed, axis-1, axis-2) are all at cosine distance 1 from axis-0 and are
            // tie-broken by EntryId ASC. We assert the distance distribution and the
            // top hit, but do not pin which of {mixed, axis-1, axis-2} lands at index 1
            // because that depends on the randomly-generated Guids and would be flaky.
            var query = MakeUnitVector(0);
            var hits = await repository.SearchNearestAsync(query, k: 4, CancellationToken.None);
            Assert.Equal(4, hits.Count);
            Assert.Equal(0d, hits[0].Distance, precision: 6);
            Assert.Equal(1d, hits[1].Distance, precision: 6);
            Assert.Equal(1d, hits[2].Distance, precision: 6);
            Assert.Equal(1d, hits[3].Distance, precision: 6);

            // Hit 0 is axis-0 (read the corresponding TalentIndexEntries row to assert
            // the slot — we don't trust Guid ordering, only the distance distribution).
            await using var db = await CreateDbContextAsync(connectionString);
            var firstRow = await db.TalentIndexEntries.AsNoTracking().SingleAsync(e => e.Id == hits[0].EntryId);
            Assert.Equal("axis-0", firstRow.DisplayName);

            // The three distance-1 hits must collectively be the three non-axis-0 rows.
            var remaining = new[] { hits[1].EntryId, hits[2].EntryId, hits[3].EntryId }
                .Select(id => db.TalentIndexEntries.AsNoTracking().Single(e => e.Id == id).DisplayName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            Assert.Equal(
                new[] { "axis-1", "axis-2", "mixed" }.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                remaining);

            // The three distance-1 hits must be tie-broken by EntryId ASC.
            Assert.True(hits[1].EntryId.CompareTo(hits[2].EntryId) < 0);
            Assert.True(hits[2].EntryId.CompareTo(hits[3].EntryId) < 0);
        }
        finally
        {
            await DeleteQuietAsync(repository, studentA0.Id);
            await DeleteQuietAsync(repository, studentA1.Id);
            await DeleteQuietAsync(repository, studentA2.Id);
            await DeleteQuietAsync(repository, studentMixed.Id);
        }
    }

    [LivePgFact]
    public async Task SearchNearestAsync_BetweenAxesOneAndTwo_OrdersCorrectly()
    {
        var connectionString = RequireConnectionString();
        var repository = CreateRepository(connectionString);

        var studentA1 = Guid.NewGuid();
        var studentA2 = Guid.NewGuid();
        var studentA3 = Guid.NewGuid();

        try
        {
            await repository.UpsertAsync(studentA1, "axis-1", null, null, null, null, "[]", "a1", "h1", MakeUnitVector(1), DateTimeOffset.UtcNow, CancellationToken.None);
            await repository.UpsertAsync(studentA2, "axis-2", null, null, null, null, "[]", "a2", "h2", MakeUnitVector(2), DateTimeOffset.UtcNow, CancellationToken.None);
            await repository.UpsertAsync(studentA3, "axis-3", null, null, null, null, "[]", "a3", "h3", MakeUnitVector(3), DateTimeOffset.UtcNow, CancellationToken.None);

            // Query halfway between axes 1 and 2: cosdist to axis-1 == cosdist to axis-2 (both
            // 0.5 normalized to 1 with equal-weight axis-1, axis-2 — see math), and cosdist
            // to axis-3 == 2. The first two should be axis-1 and axis-2 tied, axis-3 last.
            var query = MakeMixedVector(new[] { 1, 2 });
            var hits = await repository.SearchNearestAsync(query, k: 3, CancellationToken.None);
            Assert.Equal(3, hits.Count);
            // Both equal-half queries have dot product sqrt(0.5*0.5+0.5*0.5)=sqrt(0.5)
            // with each unit vector, similarity 0.5/sqrt(0.5*1)=0.707..., distance
            // 1-0.707 = 0.293. The third (axis-3) has dot 0, distance 1.
            Assert.Equal(0.29289321881345254d, hits[0].Distance, precision: 6);
            Assert.Equal(0.29289321881345254d, hits[1].Distance, precision: 6);
            Assert.Equal(1d, hits[2].Distance, precision: 6);

            // The tie between axis-1 and axis-2 must be broken deterministically by EntryId ASC.
            await using var db = await CreateDbContextAsync(connectionString);
            var first = await db.TalentIndexEntries.AsNoTracking().SingleAsync(e => e.Id == hits[0].EntryId);
            var second = await db.TalentIndexEntries.AsNoTracking().SingleAsync(e => e.Id == hits[1].EntryId);
            var third = await db.TalentIndexEntries.AsNoTracking().SingleAsync(e => e.Id == hits[2].EntryId);
            Assert.NotEqual(first.DisplayName, second.DisplayName);
            Assert.Contains(first.DisplayName, new[] { "axis-1", "axis-2" });
            Assert.Contains(second.DisplayName, new[] { "axis-1", "axis-2" });
            Assert.Equal("axis-3", third.DisplayName);
            Assert.True(hits[0].EntryId.CompareTo(hits[1].EntryId) < 0, "Tie-breaker must be EntryId ASC.");
        }
        finally
        {
            await DeleteQuietAsync(repository, studentA1);
            await DeleteQuietAsync(repository, studentA2);
            await DeleteQuietAsync(repository, studentA3);
        }
    }

    [LivePgFact]
    public async Task SearchNearestAsync_TopK_RespectsLimit()
    {
        var connectionString = RequireConnectionString();
        var repository = CreateRepository(connectionString);

        var students = new List<Guid>();
        try
        {
            for (var slot = 0; slot < 5; slot++)
            {
                var id = Guid.NewGuid();
                students.Add(id);
                await repository.UpsertAsync(id, $"axis-{slot}", null, null, null, null, "[]", $"a{slot}", $"h{slot}", MakeUnitVector(slot), DateTimeOffset.UtcNow, CancellationToken.None);
            }

            var hits = await repository.SearchNearestAsync(MakeUnitVector(0), k: 2, CancellationToken.None);
            // The LIMIT cap must apply: exactly k=2 rows returned even though 4 other
            // entries are at the same distance 1 and would otherwise be in the result.
            Assert.Equal(2, hits.Count);
            Assert.Equal(0d, hits[0].Distance, precision: 6);
            // Hit 1 is one of the four distance-1 candidates (axis-1..axis-4) — picked
            // by EntryId ASC since they're all tied. We assert on the slot name shape,
            // not on which specific axis slot landed here, because that depends on the
            // randomly-generated Guids and would be flaky.
            await using var db = await CreateDbContextAsync(connectionString);
            var firstSlot = await db.TalentIndexEntries.AsNoTracking().SingleAsync(e => e.Id == hits[0].EntryId);
            var secondSlot = await db.TalentIndexEntries.AsNoTracking().SingleAsync(e => e.Id == hits[1].EntryId);
            Assert.Equal("axis-0", firstSlot.DisplayName);
            Assert.Matches("^axis-[1-4]$", secondSlot.DisplayName);
        }
        finally
        {
            foreach (var id in students)
            {
                await DeleteQuietAsync(repository, id);
            }
        }
    }

    [LivePgFact]
    public async Task SearchNearestAsync_MatchesInMemoryOrdering_OnSameData()
    {
        var connectionString = RequireConnectionString();
        var repository = CreateRepository(connectionString);

        // Build the same dataset against the InMemory branch on a parallel
        // WriteDbContext, then compare the ordered StudentAccountIds.
        var (ids, vectors) = MakeRandomCorpus(count: 6);

        try
        {
            // Seed Postgres through the production repo.
            for (var i = 0; i < ids.Count; i++)
            {
                await repository.UpsertAsync(ids[i], $"row-{i}", null, null, null, null, "[]", $"s{i}", $"h{i}", vectors[i], DateTimeOffset.UtcNow, CancellationToken.None);
            }

            // Seed an InMemory repo on its own WriteDbContext so the brute-force branch
            // sees the same vectors without touching Postgres.
            var inMemoryOptions = new DbContextOptionsBuilder<WriteDbContext>()
                .UseInMemoryDatabase($"TalentIndexCrossValidation_{Guid.NewGuid():N}")
                .Options;
            await using var inMemoryContext = new WriteDbContext(inMemoryOptions, new AmbientAccountContext());
            var inMemoryRepository = new TalentIndexRepository(
                inMemoryContext,
                Options.Create(new ConnectionStringsOptions { WriteDb = connectionString }),
                NullLogger<TalentIndexRepository>.Instance);
            for (var i = 0; i < ids.Count; i++)
            {
                await inMemoryRepository.UpsertAsync(ids[i], $"row-{i}", null, null, null, null, "[]", $"s{i}", $"h{i}", vectors[i], DateTimeOffset.UtcNow, CancellationToken.None);
            }

            // A query that exercises every pair (mixed across all axes) — chosen so the
            // first ranked by both branches matches the same StudentAccountId.
            var query = MakeMixedVector(new[] { 0, 1, 2, 3, 4, 5 });
            var postgresHits = await repository.SearchNearestAsync(query, k: ids.Count, CancellationToken.None);
            var inMemoryHits = await inMemoryRepository.SearchNearestAsync(query, k: ids.Count, CancellationToken.None);

            // Map each EntryId back to the StudentAccountId so the comparison is robust
            // against EF generating different primary keys than the InMemory branch.
            await using var db = await CreateDbContextAsync(connectionString);
            var pgIds = new List<Guid>();
            foreach (var hit in postgresHits)
            {
                var row = await db.TalentIndexEntries.AsNoTracking().SingleAsync(e => e.Id == hit.EntryId);
                pgIds.Add(row.StudentAccountId);
            }

            var imIds = new List<Guid>();
            foreach (var hit in inMemoryHits)
            {
                var row = await inMemoryContext.TalentIndexEntries.AsNoTracking().SingleAsync(e => e.Id == hit.EntryId);
                imIds.Add(row.StudentAccountId);
            }

            // Distances match to a tolerance; the ordering is exact.
            Assert.Equal(imIds, pgIds);
            for (var i = 0; i < postgresHits.Count; i++)
            {
                Assert.Equal(inMemoryHits[i].Distance, postgresHits[i].Distance, precision: 6);
            }
        }
        finally
        {
            foreach (var id in ids)
            {
                await DeleteQuietAsync(repository, id);
            }
        }
    }

    [LivePgFact]
    public async Task DeleteAsync_UnknownStudent_DoesNotThrow()
    {
        var connectionString = RequireConnectionString();
        var repository = CreateRepository(connectionString);

        // A random Guid almost certainly has no row in the table — proves the
        // single-statement DELETE is a no-op on a missing row, not an exception.
        var missing = Guid.NewGuid();
        await repository.DeleteAsync(missing, CancellationToken.None);

        // A second call against the same Guid must also be a no-op (idempotency).
        await repository.DeleteAsync(missing, CancellationToken.None);
    }

    [LivePgFact]
    public async Task DeleteAsync_ExistingStudent_RemovesRow()
    {
        var connectionString = RequireConnectionString();
        var repository = CreateRepository(connectionString);
        var studentAccountId = Guid.NewGuid();

        await repository.UpsertAsync(
            studentAccountId,
            displayName: "to-delete",
            headline: null,
            university: null,
            fieldOfStudy: null,
            studyYear: null,
            itemsJson: "[]",
            searchText: "x",
            contentHash: "x",
            embedding: MakeUnitVector(0),
            updatedAt: DateTimeOffset.UtcNow,
            cancellationToken: CancellationToken.None);

        // Confirm the row landed before we delete it (so a regression where the
        // upsert silently fails shows up here, not at the no-rows assertion).
        await using (var dbContext = await CreateDbContextAsync(connectionString))
        {
            Assert.NotNull(
                await dbContext.TalentIndexEntries
                    .AsNoTracking()
                    .SingleOrDefaultAsync(e => e.StudentAccountId == studentAccountId));
        }

        await repository.DeleteAsync(studentAccountId, CancellationToken.None);

        await using (var dbContext = await CreateDbContextAsync(connectionString))
        {
            Assert.Null(
                await dbContext.TalentIndexEntries
                    .AsNoTracking()
                    .SingleOrDefaultAsync(e => e.StudentAccountId == studentAccountId));
        }
    }

    [LivePgFact]
    public async Task SearchNearestAsync_EmptyTable_ReturnsEmptyList()
    {
        var connectionString = RequireConnectionString();
        var repository = CreateRepository(connectionString);

        // Build a fresh InMemory repo on the same connection-string config so the
        // repository's constructor doesn't branch on the InMemory flag — this test
        // specifically exercises the Postgres path.
        var options = new DbContextOptionsBuilder<WriteDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using var dbContext = new WriteDbContext(options, new AmbientAccountContext());
        var liveRepository = new TalentIndexRepository(
            dbContext,
            Options.Create(new ConnectionStringsOptions { WriteDb = connectionString }),
            NullLogger<TalentIndexRepository>.Instance);

        var hits = await liveRepository.SearchNearestAsync(MakeUnitVector(0), k: 10, CancellationToken.None);
        Assert.Empty(hits);
    }

    [LivePgFact]
    public async Task UpsertAsync_WrongDimensionVector_ThrowsBeforeSqlRuns()
    {
        var connectionString = RequireConnectionString();
        var repository = CreateRepository(connectionString);
        var studentAccountId = Guid.NewGuid();

        // Deliberately wrong dimension — 256 instead of 768. The repository's
        // pre-SQL guard must throw LlmProviderException so the embedding client
        // never gets a partial write.
        var wrongVector = new float[256];
        for (var i = 0; i < wrongVector.Length; i++) wrongVector[i] = 0f;

        var exception = await Assert.ThrowsAsync<LlmProviderException>(async () =>
            await repository.UpsertAsync(
                studentAccountId,
                displayName: "wrong-dim",
                headline: null,
                university: null,
                fieldOfStudy: null,
                studyYear: null,
                itemsJson: "[]",
                searchText: "x",
                contentHash: "x",
                embedding: wrongVector,
                updatedAt: DateTimeOffset.UtcNow,
                cancellationToken: CancellationToken.None));

        Assert.Contains("256", exception.Message);

        // No row should have been written.
        await using var dbContext = await CreateDbContextAsync(connectionString);
        Assert.Null(
            await dbContext.TalentIndexEntries
                .AsNoTracking()
                .SingleOrDefaultAsync(e => e.StudentAccountId == studentAccountId));
    }

    [LivePgFact]
    public async Task SearchNearestAsync_WrongDimensionQuery_ThrowsBeforeSqlRuns()
    {
        var connectionString = RequireConnectionString();
        var repository = CreateRepository(connectionString);

        var wrongQuery = new float[256];
        var exception = await Assert.ThrowsAsync<LlmProviderException>(async () =>
            await repository.SearchNearestAsync(wrongQuery, k: 5, CancellationToken.None));
        Assert.Contains("256", exception.Message);
    }

    // ----- infrastructure -----

    private static string RequireConnectionString()
    {
        var value = Environment.GetEnvironmentVariable(LivePgEnvVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{LivePgEnvVar} must be set to an Npgsql connection string to run this test.");
        }
        return value;
    }

    private static TalentIndexRepository CreateRepository(string connectionString)
    {
        // Build a WriteDbContext whose provider the repository will see as the
        // Npgsql provider so the production branch is exercised.
        var options = new DbContextOptionsBuilder<WriteDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var dbContext = new WriteDbContext(options, new AmbientAccountContext());
        return new TalentIndexRepository(
            dbContext,
            Options.Create(new ConnectionStringsOptions { WriteDb = connectionString }),
            NullLogger<TalentIndexRepository>.Instance);
    }

    private static async Task<WriteDbContext> CreateDbContextAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<WriteDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var dbContext = new WriteDbContext(options, new AmbientAccountContext());
        // Force the model to build so the global query filter is wired before
        // the test issues a query.
        _ = await dbContext.Database.CanConnectAsync().ConfigureAwait(false);
        return dbContext;
    }

    private static async Task DeleteQuietAsync(TalentIndexRepository repository, Guid studentAccountId)
    {
        try
        {
            await repository.DeleteAsync(studentAccountId, CancellationToken.None);
        }
        catch
        {
            // Best-effort cleanup; ignore to avoid masking the real assertion failure.
        }
    }

    private static float[] MakeUnitVector(int slot)
    {
        var v = new float[TalentIndexConstants.EmbeddingDimensions];
        v[slot] = 1f;
        return v;
    }

    private static float[] MakeMixedVector(int[] slots)
    {
        var v = new float[TalentIndexConstants.EmbeddingDimensions];
        var weight = 1f / slots.Length;
        foreach (var slot in slots)
        {
            v[slot] = weight;
        }
        return v;
    }

    private static (List<Guid> Ids, List<float[]> Vectors) MakeRandomCorpus(int count)
    {
        // Deterministic seed — the test asserts equality with the InMemory branch,
        // so a non-seeded Random would let the test pass on luck and fail on rerun.
        var rng = new Random(Seed: 1729);
        var ids = new List<Guid>(count);
        var vectors = new List<float[]>(count);
        for (var i = 0; i < count; i++)
        {
            ids.Add(Guid.NewGuid());
            var v = new float[TalentIndexConstants.EmbeddingDimensions];
            for (var j = 0; j < v.Length; j++)
            {
                v[j] = (float)((rng.NextDouble() - 0.5d) * 2d);
            }
            vectors.Add(v);
        }
        return (ids, vectors);
    }

    /// <summary>
    /// Format the same way <c>TalentIndexRepository.FormatVectorLiteral</c> does
    /// (invariant-culture, G9) so the comparison against the pgvector text
    /// rendering is meaningful.
    /// </summary>
    private static string FormatVectorLiteralForCompare(IReadOnlyList<float> vector)
    {
        var builder = new System.Text.StringBuilder(capacity: vector.Count * 12);
        builder.Append('[');
        for (var i = 0; i < vector.Count; i++)
        {
            if (i > 0) builder.Append(',');
            builder.Append(vector[i].ToString("G9", System.Globalization.CultureInfo.InvariantCulture));
        }
        builder.Append(']');
        return builder.ToString();
    }
}

/// <summary>
/// xUnit 2.9.3 <see cref="FactAttribute"/> that sets <see cref="FactAttribute.Skip"/>
/// when <c>STORPORATE_TEST_PG</c> is not set to a non-empty Npgsql connection
/// string. The runner reports the test as <c>Skipped</c> with a self-describing
/// reason rather than <c>Passed</c> (silent skip) or <c>Failed</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class LivePgFactAttribute : FactAttribute
{
    public LivePgFactAttribute()
    {
        var connectionString = Environment.GetEnvironmentVariable("STORPORATE_TEST_PG");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Skip =
                "STORPORATE_TEST_PG is not set to an Npgsql connection string; "
                + "this live pgvector test is gated to opt-in runs against the "
                + "throwaway storporate-pgvector-test container.";
        }
    }
}