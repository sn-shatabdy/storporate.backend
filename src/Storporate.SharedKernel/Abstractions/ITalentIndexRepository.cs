namespace Storporate.SharedKernel.Abstractions;

/// <summary>
/// Provider-agnostic entry point for the <see cref="Storporate.SharedKernel.Entities.TalentIndexEntry"/>
/// search-index table (STOR-43 Phase 1 + Phase 2). The Phase 1 caller is the
/// <c>RefreshTalentIndexEntryProcessor</c> background job; the Phase 2 caller is
/// the <c>SearchTalentJobProcessor</c>. Concrete implementations live in
/// <c>Storporate.Infrastructure</c>; callers in <c>Storporate.Modules</c> depend
/// only on this interface.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the embedding column is not in the EF model.</b> The
/// <c>vector(768)</c> column is created by the migration's raw SQL and is
/// invisible to EF Core, so neither read nor write goes through
/// <see cref="Microsoft.EntityFrameworkCore.DbContext"/>'s change tracker.
/// <see cref="UpsertAsync"/> writes the row + the vector in a single raw-SQL
/// command (so the <c>INSERT ... ON CONFLICT</c> stays atomic), and
/// <see cref="SearchNearestAsync"/> returns ids + scores from a raw
/// <c>SELECT ... ORDER BY embedding &lt;=&gt; @vec</c> query.
/// </para>
/// <para>
/// <b>In-memory branch.</b> When the running <c>WriteDbContext</c>'s
/// <c>Database.ProviderName</c> is the InMemory provider the implementation
/// keeps a singleton dictionary store (so each test host has its own copy),
/// computes cosine similarity in-process over the seeded entries, and returns
/// the same shape. The two branches are never mixed within one request — the
/// branch is decided once at the constructor.
/// </para>
/// <para>
/// <b>Order contract.</b> <see cref="SearchNearestAsync"/> returns the top-K
/// nearest entries ordered by ascending cosine distance (most similar first).
/// Ties are broken by ascending <see cref="Storporate.SharedKernel.Entities.TalentIndexEntry.Id"/>
/// so the result is deterministic across two implementations and across
/// repeated calls.
/// </para>
/// </remarks>
public interface ITalentIndexRepository
{
    /// <summary>
    /// Insert or update the entry for <paramref name="studentAccountId"/> and
    /// write its 768-dimension embedding vector in a single round-trip. If a
    /// row already exists for the account it is replaced; the upsert key is the
    /// unique <c>StudentAccountId</c> index installed by the configuration.
    /// </summary>
    /// <param name="studentAccountId">The owning student's account id.</param>
    /// <param name="displayName">The projection's display name (always
    /// populated by the processor).</param>
    /// <param name="headline">Headline to store, or <c>null</c> when the
    /// student chose to hide it.</param>
    /// <param name="university">University to store, or <c>null</c> when the
    /// student chose to hide it.</param>
    /// <param name="fieldOfStudy">Field of study to store, or <c>null</c> when
    /// the student chose to hide it.</param>
    /// <param name="studyYear">Study year to store, or <c>null</c> when the
    /// student chose to hide it.</param>
    /// <param name="itemsJson">The JSON snapshot of analyzed items and skills
    /// (see <see cref="Storporate.SharedKernel.Entities.TalentIndexItemSnapshot"/>).</param>
    /// <param name="searchText">The plain-text summary that was embedded.</param>
    /// <param name="contentHash">The lowercase hex SHA-256 over the projection
    /// inputs (caller-computed).</param>
    /// <param name="embedding">The 768-dimension float vector. The
    /// implementation does not normalize — cosine distance is invariant under
    /// positive scalar multiples and pgvector's <c>&lt;=&gt;</c> operator
    /// normalizes internally.</param>
    /// <param name="updatedAt">UTC timestamp of the upsert.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task UpsertAsync(
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
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete the entry for <paramref name="studentAccountId"/> if one exists.
    /// No-op when the row is already absent (the opt-out path runs
    /// unconditionally so a second PUT never errors on a missing row).
    /// </summary>
    Task DeleteAsync(
        Guid studentAccountId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// STOR-44 Phase 1: remove the <c>original</c> descriptor of a single portfolio
    /// item from the student's <see cref="Storporate.SharedKernel.Entities.TalentIndexEntry.ItemsJson"/>
    /// without touching any other field on the entry. Used by the per-item drill-down
    /// switching path so the moment a student toggles
    /// <see cref="Storporate.SharedKernel.Entities.PortfolioItem.ShareOriginalWithEmployers"/>
    /// from <see langword="true"/> to <see langword="false"/>, the corresponding
    /// descriptor disappears from the non-tenant search index (which an Organization
    /// will read in Phase 2 — the descriptor is the only path to the original file or link).
    /// No-op when the entry or the item doesn't exist. No re-embedding: the descriptor
    /// is metadata the employer endpoint reads; the search-text and vector are unchanged.
    /// </summary>
    /// <param name="studentAccountId">The owning student's
    /// <see cref="Storporate.SharedKernel.Entities.User.Id"/>.</param>
    /// <param name="portfolioItemId">The <see cref="Storporate.SharedKernel.Entities.PortfolioItem.Id"/>
    /// whose descriptor should be removed.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ClearOriginalAsync(
        Guid studentAccountId,
        Guid portfolioItemId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Return the ids of the <paramref name="k"/> nearest entries to
    /// <paramref name="queryVector"/>, ordered by ascending cosine distance.
    /// Ties are broken by ascending <c>Id</c>. An empty store returns an empty
    /// array. The query vector length must match the configured embedding
    /// dimension; the implementation verifies this and throws
    /// <see cref="Storporate.Infrastructure.Llm.LlmProviderException"/> on a
    /// mismatch (the same exception the embedding client uses, so the caller's
    /// retry path handles it identically).
    /// </summary>
    Task<IReadOnlyList<TalentIndexSearchHit>> SearchNearestAsync(
        IReadOnlyList<float> queryVector,
        int k,
        CancellationToken cancellationToken = default);
}

/// <summary>One hit returned by
/// <see cref="ITalentIndexRepository.SearchNearestAsync"/>.</summary>
/// <param name="EntryId">The <see cref="Storporate.SharedKernel.Entities.TalentIndexEntry.Id"/>
/// of the matching entry. Surfaced to Organization callers as <c>candidateId</c>
/// by the Phase 2 response builder.</param>
/// <param name="Distance">Cosine distance to the query vector in the range
/// <c>[0, 2]</c> (<c>0</c> = identical, <c>2</c> = opposite). Phase 2
/// consumers sort on this directly — the repository never sorts the
/// collection for the caller.</param>
public sealed record TalentIndexSearchHit(Guid EntryId, double Distance);
