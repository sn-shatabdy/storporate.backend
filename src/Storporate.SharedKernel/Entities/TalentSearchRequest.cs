namespace Storporate.SharedKernel.Entities;

/// <summary>
/// A single Organization-side plain-language talent search submitted via
/// <c>POST /api/discovery/talent-searches</c>. The endpoint creates the
/// record (Pending) and a paired <see cref="Job"/> in one
/// <c>SaveChangesAsync</c>; the <c>SearchTalentJobProcessor</c> fills in
/// <see cref="ResultJson"/> / <see cref="CompletedAt"/> / <see cref="ErrorCode"/>
/// when the background work finishes. The polling
/// <c>GET /api/discovery/talent-searches/{id}</c> reads the same row under
/// the global query filter — a cross-account id never matches.
/// </summary>
/// <remarks>
/// <para>
/// <b>IAccountScoped.</b> The search belongs to the calling Organization
/// account; the global query filter installed in
/// <c>WriteDbContext.OnModelCreating</c> restricts reads to the ambient
/// account, the save-time <c>RowLevelSecurityInterceptor</c> enforces the
/// same on writes, and the <c>account_scoped</c> RLS policy installed by
/// the Phase 2 follow-up migration guards at the database. Together those
/// three layers keep one Organization's queries invisible to another.
/// </para>
/// <para>
/// <b>Why <see cref="ResultJson"/> is a raw JSON string, not a typed
/// navigation.</b> The processor rebuilds the projection from the live
/// <see cref="TalentIndexEntry"/> snapshot on every <c>GET</c> (see the
/// Phase 2 plan, re-validation step) so the response shape only exists at
/// the API edge — keeping it as a serialized blob avoids a separate
/// results table that would need a refresh path of its own.
/// </para>
/// </remarks>
public sealed class TalentSearchRequest : IAccountScoped
{
    /// <summary>Primary key. Surfaced to the caller as <c>searchId</c> on
    /// the POST response and as <c>id</c> on the GET response.</summary>
    public Guid Id { get; set; }

    /// <inheritdoc />
    public Guid AccountId { get; set; }

    /// <summary>Navigation to the owning <see cref="User"/>. Not required
    /// at insert time — EF Core populates it from <see cref="AccountId"/>
    /// when the principal is loaded.</summary>
    public User? Account { get; set; }

    /// <summary>The plain-language employer need, trimmed. Capped at
    /// 1000 characters by the request validator so a passing payload
    /// always fits the column.</summary>
    public required string QueryText { get; set; }

    /// <summary>One of <see cref="TalentSearchStatuses"/>. Pending while
    /// the search job is queued / running; Completed once the processor
    /// has stored <see cref="ResultJson"/>; Failed after retries exhaust
    /// or the reaper abandons the job.</summary>
    public string Status { get; set; } = TalentSearchStatuses.Pending;

    /// <summary>The serialized results array produced by the processor.
    /// <see langword="null"/> until <see cref="Status"/> reaches
    /// <see cref="TalentSearchStatuses.Completed"/>.</summary>
    public string? ResultJson { get; set; }

    /// <summary>Populated when <see cref="Status"/> is
    /// <see cref="TalentSearchStatuses.Failed"/>. One of
    /// <c>llm_provider_error</c> / <c>talent_search_failed</c>.</summary>
    public string? ErrorCode { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC timestamp of the terminal transition (Completed or
    /// Failed). Null while <see cref="Status"/> is Pending.</summary>
    public DateTimeOffset? CompletedAt { get; set; }
}
