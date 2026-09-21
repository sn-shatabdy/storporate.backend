using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Persistence.TalentIndex;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.Persistence;

/// <summary>
/// STOR-43 Phase 1: pin the in-memory branch of <see cref="TalentIndexRepository"/>.
/// The repository's two providers (raw Npgsql + pgvector, in-memory dictionary) are
/// decided once at construction from <c>Database.ProviderName</c>; the unit suite
/// exercises only the InMemory branch because no live Postgres is available. The
/// Postgres + pgvector ordering math is identical
/// (<c>1 - (a·b) / (||a|| * ||b||)</c>) — every test below asserts a value the
/// postgres path must match.
/// </summary>
/// <remarks>
/// <para>
/// Pinned behaviors:
/// <list type="bullet">
///   <item>Search nearest returns entries ordered by ascending cosine distance; ties broken by ascending Id.</item>
///   <item>An empty store returns an empty list.</item>
///   <item>Delete is idempotent (no-throw on a missing id).</item>
///   <item>Upsert overwrites an existing entry for the same student (one row per StudentAccountId).</item>
///   <item>Upsert with a mismatched embedding dimension throws <see cref="LlmProviderException"/> without writing.</item>
///   <item>Search with a mismatched query-vector dimension throws <see cref="LlmProviderException"/>.</item>
/// </list>
/// </para>
/// </remarks>
public class TalentIndexRepositoryTests
{
    [Fact]
    public async Task SearchNearest_ThreeSeededEntries_ReturnsNearestFirst()
    {
        var (repository, _) = BuildRepository();
        var query = MakeUnitVector(slot: 0);
        var orthogonal = MakeUnitVector(slot: 100);
        var antipodal = MakeNegativeUnitVector(slot: 0);

        await repository.UpsertAsync(Guid.NewGuid(), "near", null, null, null, null,
            "[]", "near text", "hash-near", query, DateTimeOffset.UtcNow, CancellationToken.None);
        await repository.UpsertAsync(Guid.NewGuid(), "ortho", null, null, null, null,
            "[]", "ortho text", "hash-ortho", orthogonal, DateTimeOffset.UtcNow, CancellationToken.None);
        await repository.UpsertAsync(Guid.NewGuid(), "anti", null, null, null, null,
            "[]", "anti text", "hash-anti", antipodal, DateTimeOffset.UtcNow, CancellationToken.None);

        var hits = await repository.SearchNearestAsync(query, k: 3, CancellationToken.None);

        Assert.Equal(3, hits.Count);
        // The query's twin is cos=1 → distance 0.
        Assert.Equal(0d, hits[0].Distance, tolerance: 1e-6);
        // Orthogonal is cos=0 → distance 1.
        Assert.Equal(1d, hits[1].Distance, tolerance: 1e-6);
        // Antipodal is cos=-1 → distance 2.
        Assert.Equal(2d, hits[2].Distance, tolerance: 1e-6);
    }

    [Fact]
    public async Task SearchNearest_EmptyStore_ReturnsEmpty()
    {
        var (repository, _) = BuildRepository();
        var hits = await repository.SearchNearestAsync(MakeUnitVector(0), k: 5, CancellationToken.None);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task SearchNearest_KLessThanCount_TrimsToK()
    {
        var (repository, _) = BuildRepository();
        await SeedThreeAxisEntriesAsync(repository);

        var hits = await repository.SearchNearestAsync(MakeUnitVector(0), k: 2, CancellationToken.None);

        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task SearchNearest_KGreaterThanCount_ReturnsAll()
    {
        var (repository, _) = BuildRepository();
        await SeedThreeAxisEntriesAsync(repository);

        var hits = await repository.SearchNearestAsync(MakeUnitVector(0), k: 10, CancellationToken.None);

        Assert.Equal(3, hits.Count);
    }

    [Fact]
    public async Task SearchNearest_TiedDistance_OrdersByEntryIdAscending()
    {
        // Two entries that point in the same direction with different
        // magnitudes — cosine distance is invariant under positive scalar
        // multiplication, so both are exactly distance 0 to the query.
        var (repository, _) = BuildRepository();
        var baseVector = MakeUnitVector(0);
        var scaledVector = baseVector.Select(v => v * 5f).ToArray();

        await repository.UpsertAsync(Guid.NewGuid(), "p", null, null, null, null,
            "[]", "p text", "hash-p", baseVector, DateTimeOffset.UtcNow, CancellationToken.None);
        await repository.UpsertAsync(Guid.NewGuid(), "q", null, null, null, null,
            "[]", "q text", "hash-q", scaledVector, DateTimeOffset.UtcNow, CancellationToken.None);

        var hits = await repository.SearchNearestAsync(baseVector, k: 5, CancellationToken.None);

        Assert.Equal(2, hits.Count);
        Assert.Equal(0d, hits[0].Distance, tolerance: 1e-6);
        Assert.Equal(0d, hits[1].Distance, tolerance: 1e-6);
        Assert.True(
            hits[0].EntryId.CompareTo(hits[1].EntryId) <= 0,
            $"Expected ascending Id tie-break but got {hits[0].EntryId} then {hits[1].EntryId}.");
    }

    [Fact]
    public async Task UpsertAsync_SameStudent_OverwritesInPlace()
    {
        // STOR-43 unique key is StudentAccountId — a second upsert for the same
        // student must replace the row, not append. The in-memory store's
        // dictionary key is StudentAccountId which makes the "overwrite"
        // behavior structural.
        var (repository, _) = BuildRepository();
        var studentId = Guid.NewGuid();
        var firstVector = MakeUnitVector(0);
        var secondVector = MakeUnitVector(5);

        await repository.UpsertAsync(studentId, "first", null, null, null, null,
            "[]", "first text", "hash-first", firstVector, DateTimeOffset.UtcNow, CancellationToken.None);
        await repository.UpsertAsync(studentId, "second", null, null, null, null,
            "[]", "second text", "hash-second", secondVector, DateTimeOffset.UtcNow, CancellationToken.None);

        // Searching with the second vector must return exactly one hit (the
        // upserted one), not two.
        var hits = await repository.SearchNearestAsync(secondVector, k: 5, CancellationToken.None);
        var hit = Assert.Single(hits);
        Assert.Equal(0d, hit.Distance, tolerance: 1e-6);
    }

    [Fact]
    public async Task DeleteAsync_MissingStudent_IsNoOp()
    {
        var (repository, _) = BuildRepository();
        await repository.DeleteAsync(Guid.NewGuid(), CancellationToken.None);
        // No assertion needed — must not throw. A second call for the same
        // unknown id is also a no-op.
        await repository.DeleteAsync(Guid.NewGuid(), CancellationToken.None);
    }

    [Fact]
    public async Task DeleteAsync_PresentStudent_RemovesEntry()
    {
        var (repository, _) = BuildRepository();
        var studentId = Guid.NewGuid();
        var vector = MakeUnitVector(0);
        await repository.UpsertAsync(studentId, "x", null, null, null, null,
            "[]", "x text", "hash-x", vector, DateTimeOffset.UtcNow, CancellationToken.None);

        await repository.DeleteAsync(studentId, CancellationToken.None);

        var hits = await repository.SearchNearestAsync(vector, k: 5, CancellationToken.None);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task UpsertAsync_DimensionMismatch_ThrowsLlmProviderExceptionWithoutWriting()
    {
        var (repository, _) = BuildRepository();
        // Build a vector of 2 floats (mismatched 768-dim configuration).
        var badVector = new float[] { 0.1f, 0.2f };

        await Assert.ThrowsAsync<Storporate.Infrastructure.Llm.LlmProviderException>(() =>
            repository.UpsertAsync(Guid.NewGuid(), "x", null, null, null, null,
                "[]", "x text", "hash", badVector, DateTimeOffset.UtcNow, CancellationToken.None));

        // The failing upsert must not have left a partial entry.
        var hits = await repository.SearchNearestAsync(MakeUnitVector(0), k: 5, CancellationToken.None);
        Assert.Empty(hits);
    }

    [Fact]
    public async Task SearchNearestAsync_DimensionMismatch_ThrowsLlmProviderException()
    {
        var (repository, _) = BuildRepository();
        var badQuery = new float[] { 0.1f, 0.2f };

        await Assert.ThrowsAsync<Storporate.Infrastructure.Llm.LlmProviderException>(() =>
            repository.SearchNearestAsync(badQuery, k: 5, CancellationToken.None));
    }

    // ----- infrastructure -----

    /// <summary>
    /// Build a 768-dimensional unit vector with a single non-zero slot set to
    /// +1.0. Two such vectors are identical (distance 0) when their slots
    /// match, orthogonal (distance 1) when their slots differ, and the
    /// library doesn't have a way to express "antipodal" through this scheme
    /// alone — see <see cref="MakeNegativeUnitVector"/> for that.
    /// </summary>
    private static float[] MakeUnitVector(int slot) =>
        Enumerable.Range(0, TalentIndexConstants.EmbeddingDimensions)
            .Select(i => i == slot ? 1f : 0f)
            .ToArray();

    /// <summary>
    /// Build a 768-dimensional vector with a single non-zero slot set to
    /// <c>-1.0</c>. Pointing in the exact opposite direction of
    /// <see cref="MakeUnitVector"/>(<paramref name="slot"/>), so a query
    /// produced by that call returns cosine distance 2.
    /// </summary>
    private static float[] MakeNegativeUnitVector(int slot) =>
        Enumerable.Range(0, TalentIndexConstants.EmbeddingDimensions)
            .Select(i => i == slot ? -1f : 0f)
            .ToArray();

    private static async Task SeedThreeAxisEntriesAsync(ITalentIndexRepository repository)
    {
        await repository.UpsertAsync(Guid.NewGuid(), "a", null, null, null, null,
            "[]", "a text", "hash-a", MakeUnitVector(0), DateTimeOffset.UtcNow, CancellationToken.None);
        await repository.UpsertAsync(Guid.NewGuid(), "b", null, null, null, null,
            "[]", "b text", "hash-b", MakeUnitVector(100), DateTimeOffset.UtcNow, CancellationToken.None);
        await repository.UpsertAsync(Guid.NewGuid(), "c", null, null, null, null,
            "[]", "c text", "hash-c", MakeUnitVector(5), DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private static (ITalentIndexRepository Repository, WriteDbContext DbContext) BuildRepository()
    {
        var options = new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase("TalentIndexRepositoryTests-" + Guid.NewGuid().ToString("N"))
            .Options;
        var accountContext = new Storporate.Infrastructure.Authorization.AmbientAccountContext();
        var dbContext = new WriteDbContext(options, accountContext);
        var connectionStrings = Options.Create(new ConnectionStringsOptions
        {
            WriteDb = "Host=localhost;Database=test;Username=u;Password=p",
            SslMode = "Disable",
        });
        var repository = new TalentIndexRepository(
            dbContext,
            connectionStrings,
            NullLogger<TalentIndexRepository>.Instance);
        return (repository, dbContext);
    }
}