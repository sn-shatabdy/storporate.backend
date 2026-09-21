using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Storporate.Infrastructure.Llm;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.TalentIndex;

/// <summary>
/// Default <see cref="ITalentIndexRepository"/>. Two implementations live behind
/// the same class, chosen once at construction from
/// <see cref="WriteDbContext.Database.ProviderName"/>:
/// a raw-Npgsql path against pgvector for production, and a brute-force
/// dictionary path for the in-memory test suite. Both branches share the same
/// insert / delete / top-K shape and the same dimension guard so the caller's
/// job retry path treats them uniformly.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why provider-name branching at construction.</b>
/// The repository is registered as scoped against <see cref="WriteDbContext"/>,
/// so a single instance sees a single provider for its lifetime. The branch is
/// decided once (here, in the constructor) and stored as a boolean so each call
/// pays zero allocation for the check. Mirrors the pattern
/// <c>PortfolioAnalysisJobProcessor.cs:~150</c> uses for the same reason.
/// </para>
/// <para>
/// <b>Why raw Npgsql on the production path.</b>
/// The <c>vector(768)</c> column is invisible to EF Core (it is added by the
/// migration's raw SQL and not modelled). Going through the EF change tracker
/// would require either modelling the column (which would break the InMemory
/// test boot) or using <c>Database.ExecuteSqlInterpolatedAsync</c> with a
/// parameter for the vector (which works, but opens its own connection per
/// call). Owning the connection — the same pattern <c>AuditLogWriter</c> uses —
/// keeps the parameter formatting simple (NpgSql handles the <c>vector</c>
/// encoding natively when the parameter type is <see cref="NpgsqlDbType.Unknown"/>
/// or the value is a float[]) and lets us scope a transaction around the
/// upsert + the row insert for atomicity.
/// </para>
/// <para>
/// <b>In-memory store lifetime.</b>
/// Each <see cref="WriteDbContext"/> instance is scoped per request, but the
/// underlying <see cref="ConcurrentDictionary{TKey,TValue}"/> is held in a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> keyed on the context, so two
/// concurrent test hosts (or two test classes with parallel fixtures) never
/// see each other's seeded rows. A <see cref="ConcurrentDictionary{TKey,TValue}"/>
/// alone would be process-wide and bleed across test classes.
/// </para>
/// <para>
/// <b>Cosine similarity math.</b>
/// pgvector's <c>&lt;=&gt;</c> operator computes cosine distance in the range
/// <c>[0, 2]</c> (0 = identical direction, 2 = opposite). The in-memory branch
/// computes the same number with the same formula
/// (<c>1 - (a·b) / (||a|| * ||b||)</c>) so the two branches return numerically
/// identical distances for the same vectors. The upsert path does NOT
/// normalize — cosine distance is invariant under positive scalar multiplication
/// and pgvector normalizes internally, so the on-disk and in-process results
/// stay consistent.
/// </para>
/// </remarks>
public sealed class TalentIndexRepository : ITalentIndexRepository
{
    private readonly WriteDbContext _dbContext;
    private readonly ConnectionStringsOptions _connectionStrings;
    private readonly ILogger<TalentIndexRepository> _logger;

    /// <summary>True iff the runtime is the in-memory test suite. Decided
    /// once at construction.</summary>
    private readonly bool _isInMemoryProvider;

    /// <summary>Per-context in-memory embedding store. Keyed weakly so two
    /// concurrent test hosts don't see each other's rows; the value holds
    /// everything the in-memory branch needs to satisfy the next call without
    /// touching the EF context.</summary>
    private static readonly ConditionalWeakTable<WriteDbContext, InMemoryStore> InMemoryStores = new();

    public TalentIndexRepository(
        WriteDbContext dbContext,
        IOptions<ConnectionStringsOptions> connectionStrings,
        ILogger<TalentIndexRepository> logger)
    {
        _dbContext = dbContext;
        _connectionStrings = connectionStrings.Value;
        _logger = logger;
        _isInMemoryProvider = dbContext.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <inheritdoc />
    public Task UpsertAsync(
        Guid studentAccountId,
        string displayName,
        string? headline,
        string? university,
        string? fieldOfStudy,
        int? studyYear,
        string itemsJson,
        string searchText,
        string contentHash,
        IReadOnlyList<float> embedding,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default)
    {
        if (embedding.Count != TalentIndexConstants.EmbeddingDimensions)
        {
            throw new LlmProviderException(
                $"Embedding vector has {embedding.Count} dimensions; expected " +
                $"{TalentIndexConstants.EmbeddingDimensions}.");
        }

        return _isInMemoryProvider
            ? UpsertInMemoryAsync(studentAccountId, displayName, headline, university,
                fieldOfStudy, studyYear, itemsJson, searchText, contentHash, embedding,
                updatedAt)
            : UpsertPostgresAsync(studentAccountId, displayName, headline, university,
                fieldOfStudy, studyYear, itemsJson, searchText, contentHash, embedding,
                updatedAt, cancellationToken);
    }

    private async Task UpsertPostgresAsync(
        Guid studentAccountId,
        string displayName,
        string? headline,
        string? university,
        string? fieldOfStudy,
        int? studyYear,
        string itemsJson,
        string searchText,
        string contentHash,
        IReadOnlyList<float> embedding,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var connectionString = _connectionStrings.ToNpgsqlConnectionString();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using var transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);

        // Single round-trip: the INSERT ... ON CONFLICT replaces any existing
        // row in the same statement that writes the vector, so a partial upsert
        // (row written, vector missing) is structurally impossible. The
        // vector literal is rendered as a pgvector-canonical string —
        // bracketed, comma-separated floats — and sent as text; Npgsql
        // auto-casts text -> vector for the parameter typed Unknown.
        const string sql = """
            INSERT INTO "TalentIndexEntries" (
                "Id", "StudentAccountId", "DisplayName", "Headline", "University",
                "FieldOfStudy", "StudyYear", "ItemsJson", "SearchText",
                "ContentHash", "UpdatedAt", "Embedding"
            )
            VALUES (
                @id, @studentAccountId, @displayName, @headline, @university,
                @fieldOfStudy, @studyYear, @itemsJson::jsonb, @searchText,
                @contentHash, @updatedAt, @embedding::vector
            )
            ON CONFLICT ("StudentAccountId") DO UPDATE SET
                "Id" = EXCLUDED."Id",
                "DisplayName" = EXCLUDED."DisplayName",
                "Headline" = EXCLUDED."Headline",
                "University" = EXCLUDED."University",
                "FieldOfStudy" = EXCLUDED."FieldOfStudy",
                "StudyYear" = EXCLUDED."StudyYear",
                "ItemsJson" = EXCLUDED."ItemsJson",
                "SearchText" = EXCLUDED."SearchText",
                "ContentHash" = EXCLUDED."ContentHash",
                "UpdatedAt" = EXCLUDED."UpdatedAt",
                "Embedding" = EXCLUDED."Embedding";
            """;

        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter("@id", NpgsqlDbType.Uuid) { Value = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter("@studentAccountId", NpgsqlDbType.Uuid) { Value = studentAccountId });
        command.Parameters.Add(new NpgsqlParameter("@displayName", NpgsqlDbType.Varchar) { Value = displayName });
        command.Parameters.Add(new NpgsqlParameter("@headline", NpgsqlDbType.Varchar) { Value = (object?)headline ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("@university", NpgsqlDbType.Varchar) { Value = (object?)university ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("@fieldOfStudy", NpgsqlDbType.Varchar) { Value = (object?)fieldOfStudy ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("@studyYear", NpgsqlDbType.Integer) { Value = (object?)studyYear ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("@itemsJson", NpgsqlDbType.Jsonb) { Value = itemsJson });
        command.Parameters.Add(new NpgsqlParameter("@searchText", NpgsqlDbType.Text) { Value = searchText });
        command.Parameters.Add(new NpgsqlParameter("@contentHash", NpgsqlDbType.Varchar) { Value = contentHash });
        command.Parameters.Add(new NpgsqlParameter("@updatedAt", NpgsqlDbType.TimestampTz) { Value = updatedAt });
        command.Parameters.Add(new NpgsqlParameter("@embedding", NpgsqlDbType.Unknown) { Value = FormatVectorLiteral(embedding) });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Upserted talent index entry for student {StudentAccountId} ({ItemCount} items, hash {Hash}).",
            studentAccountId,
            "<items>",
            contentHash);
    }

    private async Task UpsertInMemoryAsync(
        Guid studentAccountId,
        string displayName,
        string? headline,
        string? university,
        string? fieldOfStudy,
        int? studyYear,
        string itemsJson,
        string searchText,
        string contentHash,
        IReadOnlyList<float> embedding,
        DateTimeOffset updatedAt)
    {
        var store = GetOrAddInMemoryStore();

        // Defensive copy — the caller's vector might be a reusable buffer the
        // next batch overwrites. Storing the reference would let an in-flight
        // batch pollute an already-stored entry.
        var vectorCopy = embedding.ToArray();

        var entryId = Guid.NewGuid();
        store.Entries[studentAccountId] = new InMemoryEntry(
            EntryId: entryId,
            DisplayName: displayName,
            Headline: headline,
            University: university,
            FieldOfStudy: fieldOfStudy,
            StudyYear: studyYear,
            ItemsJson: itemsJson,
            SearchText: searchText,
            ContentHash: contentHash,
            UpdatedAt: updatedAt,
            Embedding: vectorCopy);

        // Mirror the upsert into the EF context so unit-test callers can
        // verify the write by querying the TalentIndexEntries DbSet. The
        // production pgvector path doesn't need this (the raw SQL writes the
        // row directly), but the InMemory branch uses a side dictionary for
        // the vector; without this shadow row the test context would see
        // nothing under Db.TalentIndexEntries and the verification would
        // silently pass when it should fail. The vector column is intentionally
        // absent from the EF model, so only the scalar fields are mirrored.
        var existing = await _dbContext.TalentIndexEntries
            .FirstOrDefaultAsync(e => e.StudentAccountId == studentAccountId)
            .ConfigureAwait(false);
        if (existing is null)
        {
            _dbContext.TalentIndexEntries.Add(new TalentIndexEntry
            {
                Id = entryId,
                StudentAccountId = studentAccountId,
                DisplayName = displayName,
                Headline = headline,
                University = university,
                FieldOfStudy = fieldOfStudy,
                StudyYear = studyYear,
                ItemsJson = itemsJson,
                SearchText = searchText,
                ContentHash = contentHash,
                UpdatedAt = updatedAt,
            });
        }
        else
        {
            existing.DisplayName = displayName;
            existing.Headline = headline;
            existing.University = university;
            existing.FieldOfStudy = fieldOfStudy;
            existing.StudyYear = studyYear;
            existing.ItemsJson = itemsJson;
            existing.SearchText = searchText;
            existing.ContentHash = contentHash;
            existing.UpdatedAt = updatedAt;
        }
        await _dbContext.SaveChangesAsync().ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DeleteAsync(
        Guid studentAccountId,
        CancellationToken cancellationToken = default)
    {
        return _isInMemoryProvider
            ? DeleteInMemoryAsync(studentAccountId)
            : DeletePostgresAsync(studentAccountId, cancellationToken);
    }

    private async Task DeletePostgresAsync(Guid studentAccountId, CancellationToken cancellationToken)
    {
        var connectionString = _connectionStrings.ToNpgsqlConnectionString();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // Single-statement delete; ON CONFLICT-style idempotency isn't a thing
        // for DELETE so we just rely on a missing row being a no-op.
        const string sql = """
            DELETE FROM "TalentIndexEntries" WHERE "StudentAccountId" = @studentAccountId;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("@studentAccountId", NpgsqlDbType.Uuid) { Value = studentAccountId });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug("Deleted talent index entry for student {StudentAccountId}.", studentAccountId);
    }

    private async Task DeleteInMemoryAsync(Guid studentAccountId)
    {
        var store = GetOrAddInMemoryStore();
        store.Entries.TryRemove(studentAccountId, out _);

        // Mirror the delete into the EF context (same justification as the
        // upsert branch — the unit suite verifies by querying the
        // TalentIndexEntries DbSet on the same WriteDbContext).
        var existing = await _dbContext.TalentIndexEntries
            .FirstOrDefaultAsync(e => e.StudentAccountId == studentAccountId)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            _dbContext.TalentIndexEntries.Remove(existing);
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task ClearOriginalAsync(
        Guid studentAccountId,
        Guid portfolioItemId,
        CancellationToken cancellationToken = default)
    {
        return _isInMemoryProvider
            ? ClearOriginalInMemoryAsync(studentAccountId, portfolioItemId)
            : ClearOriginalPostgresAsync(studentAccountId, portfolioItemId, cancellationToken);
    }

    /// <summary>
    /// Postgres-side single-item descriptor clear. Uses <c>jsonb_agg</c> over
    /// <c>jsonb_array_elements</c> to re-build <c>ItemsJson</c> with the
    /// matching element's <c>original</c> key removed via the <c>jsonb -
    /// text</c> subtraction operator. The <c>WHERE EXISTS</c> predicate
    /// guards against touching rows that have no matching item or no
    /// <c>original</c> key — a missing row, a missing item, and an item
    /// without a descriptor are all silent no-ops, matching the documented
    /// contract.
    /// </summary>
    private async Task ClearOriginalPostgresAsync(
        Guid studentAccountId,
        Guid portfolioItemId,
        CancellationToken cancellationToken)
    {
        var connectionString = _connectionStrings.ToNpgsqlConnectionString();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // One statement, two passes:
        //   1. jsonb_agg rebuilds the array, applying the `jsonb -
        //      'original'` subtraction to the element whose
        //      "portfolioItemId" matches the parameter. Elements with a
        //      different id pass through unchanged.
        //   2. WHERE EXISTS short-circuits the whole UPDATE when the
        //      student has no row, no matching item, or the item has no
        //      "original" key — those are silent no-ops.
        // The other fields on the row are untouched (no re-embedding).
        const string sql = """
            UPDATE "TalentIndexEntries" AS t
            SET "ItemsJson" = (
                SELECT jsonb_agg(
                    CASE
                        WHEN (elem->>'portfolioItemId') = @portfolioItemId::text
                            THEN elem - 'original'
                        ELSE elem
                    END
                )
                FROM jsonb_array_elements(t."ItemsJson") AS elem
            )
            WHERE t."StudentAccountId" = @studentAccountId
              AND EXISTS (
                  SELECT 1
                  FROM jsonb_array_elements(t."ItemsJson") AS e(item)
                  WHERE (item->>'portfolioItemId') = @portfolioItemId::text
                        AND (item ? 'original')
              );
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("@studentAccountId", NpgsqlDbType.Uuid) { Value = studentAccountId });
        command.Parameters.Add(new NpgsqlParameter("@portfolioItemId", NpgsqlDbType.Uuid) { Value = portfolioItemId });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "Cleared original descriptor for portfolio item {PortfolioItemId} under student {StudentAccountId}.",
            portfolioItemId,
            studentAccountId);
    }

    /// <summary>
    /// In-memory twin of <see cref="ClearOriginalPostgresAsync"/>. Walks the
    /// stored <see cref="TalentIndexEntry.ItemsJson"/> JSON, finds the element
    /// with the matching <c>portfolioItemId</c>, removes its <c>original</c>
    /// property (if present), and re-serialises the array. Then mirrors the
    /// change into the EF context so the unit suite's
    /// <c>DbContext.TalentIndexEntries</c> query sees the updated blob.
    /// </summary>
    private async Task ClearOriginalInMemoryAsync(
        Guid studentAccountId,
        Guid portfolioItemId)
    {
        var store = GetOrAddInMemoryStore();
        if (!store.Entries.TryGetValue(studentAccountId, out var entry))
        {
            return;
        }

        // Match the production jsonb_set behaviour: a missing element OR a
        // missing "original" property is a no-op. Walk the array, rebuild it
        // with the matching element's "original" stripped.
        var snapshots = System.Text.Json.JsonSerializer.Deserialize<
            List<System.Text.Json.JsonElement>>(entry.ItemsJson)
            ?? new List<System.Text.Json.JsonElement>();

        var portfolioItemIdString = portfolioItemId.ToString();
        var mutated = false;
        var rebuilt = new List<System.Text.Json.JsonElement>(snapshots.Count);
        foreach (var element in snapshots)
        {
            var hasMatchingId = element.TryGetProperty("portfolioItemId", out var idProp)
                && idProp.ValueKind == System.Text.Json.JsonValueKind.String
                && string.Equals(idProp.GetString(), portfolioItemIdString, StringComparison.Ordinal);
            if (hasMatchingId
                && element.TryGetProperty("original", out _))
            {
                // Strip the "original" property by re-serialising the element
                // without it. We do this by cloning the element's properties
                // minus "original" into a fresh anonymous-shaped dictionary
                // and re-parsing — JsonElement itself is read-only by design.
                var pruned = PruneOriginalProperty(element);
                rebuilt.Add(pruned);
                mutated = true;
            }
            else
            {
                rebuilt.Add(element);
            }
        }

        if (!mutated)
        {
            return;
        }

        var newItemsJson = System.Text.Json.JsonSerializer.Serialize(rebuilt);
        var updated = entry with { ItemsJson = newItemsJson };
        store.Entries[studentAccountId] = updated;

        var existing = await _dbContext.TalentIndexEntries
            .FirstOrDefaultAsync(e => e.StudentAccountId == studentAccountId)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            existing.ItemsJson = newItemsJson;
            await _dbContext.SaveChangesAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Clone a <see cref="System.Text.Json.JsonElement"/> into a new one with
    /// the <c>original</c> property removed. Reads the element's own
    /// properties via <see cref="System.Text.Json.JsonElement.EnumerateObject"/>
    /// and writes them into a fresh dictionary, skipping the one we want to
    /// strip. The result is round-tripped through <see cref="System.Text.Json.JsonSerializer"/>
    /// so the caller gets a fresh <see cref="System.Text.Json.JsonElement"/> it can
    /// keep alongside the others in the rebuilt list.
    /// </summary>
    private static System.Text.Json.JsonElement PruneOriginalProperty(
        System.Text.Json.JsonElement element)
    {
        var dict = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, "original", StringComparison.Ordinal))
            {
                continue;
            }
            dict[property.Name] = property.Value.Clone();
        }
        var serialized = System.Text.Json.JsonSerializer.Serialize(dict);
        using var doc = System.Text.Json.JsonDocument.Parse(serialized);
        return doc.RootElement.Clone();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<TalentIndexSearchHit>> SearchNearestAsync(
        IReadOnlyList<float> queryVector,
        int k,
        CancellationToken cancellationToken = default)
    {
        if (queryVector.Count != TalentIndexConstants.EmbeddingDimensions)
        {
            throw new LlmProviderException(
                $"Query vector has {queryVector.Count} dimensions; expected " +
                $"{TalentIndexConstants.EmbeddingDimensions}.");
        }

        return _isInMemoryProvider
            ? Task.FromResult(SearchNearestInMemory(queryVector, k))
            : SearchNearestPostgresAsync(queryVector, k, cancellationToken);
    }

    private async Task<IReadOnlyList<TalentIndexSearchHit>> SearchNearestPostgresAsync(
        IReadOnlyList<float> queryVector,
        int k,
        CancellationToken cancellationToken)
    {
        var connectionString = _connectionStrings.ToNpgsqlConnectionString();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // ORDER BY embedding <=> @vec gives ascending cosine distance. LIMIT @k
        // caps the result; an under-populated store returns whatever rows
        // exist, ordered by distance.
        const string sql = """
            SELECT "Id" AS EntryId, "Embedding" <=> @queryVector AS Distance
            FROM "TalentIndexEntries"
            ORDER BY "Embedding" <=> @queryVector ASC, "Id" ASC
            LIMIT @k;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.Add(new NpgsqlParameter("@queryVector", NpgsqlDbType.Unknown) { Value = FormatVectorLiteral(queryVector) });
        command.Parameters.Add(new NpgsqlParameter("@k", NpgsqlDbType.Integer) { Value = k });

        var hits = new List<TalentIndexSearchHit>(capacity: Math.Min(k, 16));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            hits.Add(new TalentIndexSearchHit(
                EntryId: reader.GetGuid(0),
                Distance: reader.GetDouble(1)));
        }

        return hits;
    }

    private IReadOnlyList<TalentIndexSearchHit> SearchNearestInMemory(
        IReadOnlyList<float> queryVector,
        int k)
    {
        var store = GetOrAddInMemoryStore();
        if (store.Entries.IsEmpty || k <= 0)
        {
            return Array.Empty<TalentIndexSearchHit>();
        }

        var queryNorm = L2Norm(queryVector);

        // Compute the distance to every seeded entry once, then sort and
        // take the top-K. The store is small enough (the table is per-student,
        // bounded by the active search pool) that brute-force is fine here —
        // pgvector's HNSW index is what makes the production path scale.
        var scored = new List<(InMemoryEntry Entry, double Distance)>(store.Entries.Count);
        foreach (var entry in store.Entries.Values)
        {
            scored.Add((entry, CosineDistance(queryVector, queryNorm, entry.Embedding)));
        }

        scored.Sort(static (left, right) =>
        {
            var distanceComparison = left.Distance.CompareTo(right.Distance);
            return distanceComparison != 0
                ? distanceComparison
                : left.Entry.EntryId.CompareTo(right.Entry.EntryId);
        });

        var take = Math.Min(k, scored.Count);
        var result = new List<TalentIndexSearchHit>(take);
        for (var i = 0; i < take; i++)
        {
            result.Add(new TalentIndexSearchHit(scored[i].Entry.EntryId, scored[i].Distance));
        }
        return result;
    }

    /// <summary>
    /// pgvector accepts a vector literal in the bracketed form
    /// <c>[v1,v2,...]</c> with InvariantCulture float formatting. Rendering it
    /// client-side avoids a per-parameter Npgsql encoder round-trip and lets
    /// us reuse the same literal in the in-memory branch (which never sees
    /// Postgres but still logs it for diagnostics).
    /// </summary>
    private static string FormatVectorLiteral(IReadOnlyList<float> vector)
    {
        var builder = new System.Text.StringBuilder(capacity: vector.Count * 12);
        builder.Append('[');
        for (var i = 0; i < vector.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }
            // InvariantCulture so a comma-as-decimal-separator locale can't
            // smuggle a stray comma into the literal and break the parse.
            builder.Append(vector[i].ToString("G9", CultureInfo.InvariantCulture));
        }
        builder.Append(']');
        return builder.ToString();
    }

    /// <summary>cosine distance = 1 - cos(theta) = 1 - (a·b) / (||a|| * ||b||).</summary>
    private static double CosineDistance(
        IReadOnlyList<float> query,
        double queryNorm,
        IReadOnlyList<float> candidate)
    {
        var dot = 0d;
        var candidateNormSquared = 0d;
        for (var i = 0; i < query.Count; i++)
        {
            dot += (double)query[i] * candidate[i];
            candidateNormSquared += (double)candidate[i] * candidate[i];
        }
        var candidateNorm = Math.Sqrt(candidateNormSquared);
        if (queryNorm == 0d || candidateNorm == 0d)
        {
            // A zero-norm vector is by convention at maximum distance from
            // every non-zero vector. Mirrors pgvector, which returns 0 for
            // a zero-vs-zero pair (we follow the same convention: same
            // direction, just both empty).
            return queryNorm == 0d && candidateNorm == 0d ? 0d : 1d;
        }
        var similarity = dot / (queryNorm * candidateNorm);
        // Clamp to [-1, 1] to absorb floating-point error before subtracting
        // from 1; without the clamp a distance can land slightly above 2 and
        // break downstream assertions.
        if (similarity > 1d) similarity = 1d;
        else if (similarity < -1d) similarity = -1d;
        return 1d - similarity;
    }

    private static double L2Norm(IReadOnlyList<float> vector)
    {
        var sum = 0d;
        for (var i = 0; i < vector.Count; i++)
        {
            sum += (double)vector[i] * vector[i];
        }
        return Math.Sqrt(sum);
    }

    private InMemoryStore GetOrAddInMemoryStore()
    {
        return InMemoryStores.GetOrCreateValue(_dbContext);
    }

    /// <summary>Per-context in-memory embedding store.</summary>
    private sealed class InMemoryStore
    {
        public ConcurrentDictionary<Guid, InMemoryEntry> Entries { get; } = new();
    }

    /// <summary>One stored entry in the in-memory branch. Mirrors the
    /// production row's fields except for the storage representation of the
    /// vector (a plain float[] vs the pgvector column).</summary>
    private sealed record InMemoryEntry(
        Guid EntryId,
        string DisplayName,
        string? Headline,
        string? University,
        string? FieldOfStudy,
        int? StudyYear,
        string ItemsJson,
        string SearchText,
        string ContentHash,
        DateTimeOffset UpdatedAt,
        float[] Embedding);
}
