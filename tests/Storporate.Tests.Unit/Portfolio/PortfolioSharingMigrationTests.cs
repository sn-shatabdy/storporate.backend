using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Storporate.Infrastructure.Persistence.TalentIndex;
using Storporate.SharedKernel.Entities;
// LivePgFactAttribute lives in the Storporate.Tests.Unit.TalentIndex
// namespace (the test class it ships in for the existing
// TalentIndexRepositoryPostgresTests). Use the fully-qualified name
// rather than re-declaring the attribute.
using LivePgFact = Storporate.Tests.Unit.TalentIndex.LivePgFactAttribute;

namespace Storporate.Tests.Unit.Portfolio;

/// <summary>
/// Live pgvector-backed proof for STOR-44 Phase 1's column-level schema
/// change and the production Postgres path of
/// <see cref="TalentIndexRepository.ClearOriginalAsync"/>. Gated on
/// <c>STORPORATE_TEST_PG</c> set to a non-empty Npgsql connection string;
/// every test reports as <c>Skipped</c> when the env var is missing so
/// CI hosts without a reachable Postgres stay green and the full
/// assertion path only runs against the throwaway
/// <c>storporate-pgvector-test</c> container.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why live tests for this story.</b> The Phase 1 surface includes a
/// column add + a Postgres-only <c>jsonb_set</c> update on the new column
///'s <c>ItemsJson</c> child array. The InMemory suite already covers the
/// happy / null-result / wrong-id paths; this file proves the SQL is
/// real, the path syntax is correct, and the migration produces the
/// expected schema shape.
/// </para>
/// <para>
/// <b>Per-test isolation.</b> Each test cleans up its own seeded row(s)
/// in <c>try/finally</c>. The column-existence test uses an isolated
/// <c>pg_attribute</c> SELECT and never writes; the ClearOriginal tests
/// round-trip via a fresh <see cref="TalentIndexRepository"/> and delete
/// via the existing <c>DeleteAsync</c> path.
/// </para>
/// </remarks>
public class PortfolioSharingMigrationTests
{
    private const string LivePgEnvVar = "STORPORATE_TEST_PG";

    [LivePgFact]
    public async Task PortfolioItems_ShareOriginalColumn_IsBooleanNotNullDefaultFalse()
    {
        var connectionString = RequireConnectionString();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        // pg_attribute + pg_type + pg_attrdef join — the canonical way to
        // verify a column exists, has the right type, and carries the right
        // default. is_nullable='NO' is the NOT NULL check; the pg_attrdef
        // join confirms the DEFAULT false expression is wired.
        await using var command = new NpgsqlCommand(
            """
            SELECT
                a.atttypid::regtype::text AS data_type,
                a.attnotnull AS not_null,
                pg_get_expr(d.adbin, d.adrelid) AS default_expr
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            LEFT JOIN pg_attrdef d
                ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE c.relname = 'PortfolioItems'
              AND a.attname = 'ShareOriginalWithEmployers';
            """,
            connection);

        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "ShareOriginalWithEmployers column must exist on PortfolioItems.");

        var dataType = reader.GetString(0);
        var notNull = reader.GetBoolean(1);
        var defaultExpr = reader.IsDBNull(2) ? null : reader.GetString(2);

        Assert.Equal("boolean", dataType);
        Assert.True(notNull, "ShareOriginalWithEmployers must be NOT NULL.");
        Assert.False(string.IsNullOrWhiteSpace(defaultExpr), "ShareOriginalWithEmployers must carry a DEFAULT expression.");
        // pg_get_expr normalizes the constant literal; the boolean default
        // shows up as either "false" or "false::boolean" depending on the
        // server version. Accept either.
        Assert.True(
            defaultExpr is "false" or "false::boolean",
            $"ShareOriginalWithEmployers default must be false, got '{defaultExpr}'.");
    }

    [LivePgFact]
    public async Task ClearOriginalAsync_OnItemWithDescriptor_RemovesOnlyThatItem()
    {
        // Three items in ItemsJson: item A has an `original` descriptor,
        // item B has its own descriptor (different keys), item C has no
        // descriptor at all. Clear on item A must remove only A's
        // descriptor and leave B + C untouched.
        var connectionString = RequireConnectionString();
        var repository = CreateRepository(connectionString);
        var studentAccountId = Guid.NewGuid();
        var itemA = Guid.NewGuid();
        var itemB = Guid.NewGuid();
        var itemC = Guid.NewGuid();

        var itemsJson = System.Text.Json.JsonSerializer.Serialize(new object[]
        {
            new
            {
                portfolioItemId = itemA,
                label = "Item A",
                category = "Document",
                skills = new[] { new { name = "C#", band = "Strong", reason = "x" } },
                @original = new
                {
                    kind = "File",
                    fileName = "a.pdf",
                    contentType = "application/pdf",
                    sizeBytes = 123L,
                    storageKey = "artifacts/a/a.pdf",
                    url = (string?)null,
                },
            },
            new
            {
                portfolioItemId = itemB,
                label = "Item B",
                category = "Link",
                skills = new[] { new { name = "SQL", band = "Strong", reason = "y" } },
                @original = new
                {
                    kind = "Link",
                    fileName = (string?)null,
                    contentType = (string?)null,
                    sizeBytes = (long?)null,
                    storageKey = (string?)null,
                    url = "https://example.com/b",
                },
            },
            new
            {
                portfolioItemId = itemC,
                label = "Item C",
                category = "Document",
                skills = new[] { new { name = "React", band = "Strong", reason = "z" } },
                // intentionally no `original` key — flag-off shape
            },
        });

        try
        {
            await repository.UpsertAsync(
                studentAccountId,
                displayName: "Clear Test",
                headline: null,
                university: null,
                fieldOfStudy: null,
                studyYear: null,
                itemsJson: itemsJson,
                searchText: "clear test",
                contentHash: "h-clear",
                embedding: MakeUnitVector(0),
                updatedAt: DateTimeOffset.UtcNow,
                cancellationToken: CancellationToken.None);

            // Clear the descriptor for item A only.
            await repository.ClearOriginalAsync(studentAccountId, itemA, CancellationToken.None);

            // Read ItemsJson back and verify the per-item shape.
            var newItemsJson = await ReadItemsJsonAsync(connectionString, studentAccountId);
            using var doc = System.Text.Json.JsonDocument.Parse(newItemsJson);
            Assert.Equal(3, doc.RootElement.GetArrayLength());

            var elementA = FindItem(doc.RootElement, itemA);
            var elementB = FindItem(doc.RootElement, itemB);
            var elementC = FindItem(doc.RootElement, itemC);

            // Item A: `original` property is gone.
            Assert.False(
                elementA.TryGetProperty("original", out _),
                "Item A's `original` descriptor must be cleared.");
            // The other fields on item A are intact.
            Assert.Equal("Item A", elementA.GetProperty("label").GetString());
            Assert.Equal(itemA, elementA.GetProperty("portfolioItemId").GetGuid());

            // Item B: `original` is untouched.
            Assert.True(elementB.TryGetProperty("original", out var bOriginal));
            Assert.Equal("Link", bOriginal.GetProperty("kind").GetString());
            Assert.Equal("https://example.com/b", bOriginal.GetProperty("url").GetString());

            // Item C: never had `original`, still doesn't.
            Assert.False(elementC.TryGetProperty("original", out _));
        }
        finally
        {
            await DeleteQuietAsync(repository, studentAccountId);
        }
    }

    [LivePgFact]
    public async Task ClearOriginalAsync_OnUnknownItemId_IsNoOp()
    {
        // No items in ItemsJson match the supplied portfolioItemId; the
        // UPDATE must touch zero rows. A subsequent SELECT sees the row
        // unchanged.
        var connectionString = RequireConnectionString();
        var repository = CreateRepository(connectionString);
        var studentAccountId = Guid.NewGuid();
        var realItemId = Guid.NewGuid();
        var unknownItemId = Guid.NewGuid();

        var itemsJson = System.Text.Json.JsonSerializer.Serialize(new object[]
        {
            new
            {
                portfolioItemId = realItemId,
                label = "Real",
                category = "Document",
                skills = new[] { new { name = "Go", band = "Strong", reason = "x" } },
                @original = new
                {
                    kind = "File",
                    fileName = "real.pdf",
                    contentType = "application/pdf",
                    sizeBytes = 1L,
                    storageKey = "artifacts/real.pdf",
                    url = (string?)null,
                },
            },
        });

        try
        {
            await repository.UpsertAsync(
                studentAccountId,
                displayName: "No-op Test",
                headline: null,
                university: null,
                fieldOfStudy: null,
                studyYear: null,
                itemsJson: itemsJson,
                searchText: "no-op",
                contentHash: "h-noop",
                embedding: MakeUnitVector(0),
                updatedAt: DateTimeOffset.UtcNow,
                cancellationToken: CancellationToken.None);

            // Call must not throw — the WHERE NOT EXISTS subquery makes the
            // entire UPDATE match zero rows.
            await repository.ClearOriginalAsync(studentAccountId, unknownItemId, CancellationToken.None);

            var newItemsJson = await ReadItemsJsonAsync(connectionString, studentAccountId);
            // Compare structurally rather than byte-for-byte — Postgres
            // jsonb renders with whitespace after colons and the EF path
            // doesn't, but the JSON value is identical. The real assertion
            // is "the original descriptor is still there".
            using var doc = System.Text.Json.JsonDocument.Parse(newItemsJson);
            var root = doc.RootElement;
            Assert.Equal(1, root.GetArrayLength());
            var element = root[0];
            Assert.Equal(realItemId, element.GetProperty("portfolioItemId").GetGuid());
            Assert.True(element.TryGetProperty("original", out var originalProp));
            Assert.Equal("File", originalProp.GetProperty("kind").GetString());
            Assert.Equal("real.pdf", originalProp.GetProperty("fileName").GetString());
        }
        finally
        {
            await DeleteQuietAsync(repository, studentAccountId);
        }
    }

    [LivePgFact]
    public async Task ClearOriginalAsync_OnUnknownStudentAccountId_IsNoOp()
    {
        // Random Guid with no row in the table. The UPDATE's
        // StudentAccountId predicate must match zero rows.
        var connectionString = RequireConnectionString();
        var repository = CreateRepository(connectionString);

        var missingStudent = Guid.NewGuid();
        var someItemId = Guid.NewGuid();

        // Call must not throw even when no row exists for the student.
        await repository.ClearOriginalAsync(missingStudent, someItemId, CancellationToken.None);
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
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<
            Storporate.Infrastructure.Persistence.WriteDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var dbContext = new Storporate.Infrastructure.Persistence.WriteDbContext(
            options,
            new Storporate.Infrastructure.Authorization.AmbientAccountContext());
        return new TalentIndexRepository(
            dbContext,
            Options.Create(new Storporate.Infrastructure.Persistence.ConnectionStringsOptions
            {
                WriteDb = connectionString,
                SslMode = "Disable",
            }),
            NullLogger<TalentIndexRepository>.Instance);
    }

    private static async Task<string> ReadItemsJsonAsync(
        string connectionString,
        Guid studentAccountId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT \"ItemsJson\"::text FROM \"TalentIndexEntries\" WHERE \"StudentAccountId\" = @id",
            connection);
        command.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Uuid) { Value = studentAccountId });
        var raw = (string?)(await command.ExecuteScalarAsync());
        Assert.False(string.IsNullOrEmpty(raw), "TalentIndexEntries row must exist for the test student.");
        return raw!;
    }

    private static System.Text.Json.JsonElement FindItem(
        System.Text.Json.JsonElement root,
        Guid portfolioItemId)
    {
        foreach (var element in root.EnumerateArray())
        {
            if (element.TryGetProperty("portfolioItemId", out var idProp)
                && idProp.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(idProp.GetString(), portfolioItemId.ToString(), StringComparison.Ordinal))
            {
                return element;
            }
        }
        throw new InvalidOperationException(
            $"ItemsJson does not contain an element for portfolioItemId {portfolioItemId}.");
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
}
