using Storporate.SharedKernel.Pagination;

namespace Storporate.Modules.SecurityGovernance;

/// <summary>
/// Query-string bound pagination + filter request for <c>GET /api/security-governance/audit-log</c>.
/// Inherits <see cref="PageRequest"/>'s four pagination fields (<c>pageNumber</c>,
/// <c>pageSize</c>, <c>sortBy</c>, <c>sortDescending</c>) and adds the audit-specific
/// filter fields the endpoint accepts.
/// </summary>
/// <remarks>
/// <para>
/// Bound by ASP.NET Core's minimal-API parameter binding via the <c>[AsParameters]</c>
/// attribute on the endpoint handler. Property casing is ordinal, case-insensitive —
/// <c>action</c>, <c>Action</c>, and <c>ACTION</c> all bind to the same field.
/// </para>
/// <para>
/// Every filter field is nullable: <see langword="null"/> means "no filter on this
/// field", letting a single endpoint shape serve both the unfiltered "show me
/// everything" and the fully-filtered "show me every <c>login_failed</c> in the last 24h
/// from this account" call patterns without a parallel endpoint per filter
/// combination.
/// </para>
/// </remarks>
public sealed record ListAuditLogEntriesRequest : PageRequest
{
    /// <summary>Exact-match filter on <see cref="Storporate.SharedKernel.Entities.AuditLogEntry.Action"/>.
    /// Case-sensitive: action strings are normalized to a small fixed vocabulary at the
    /// write site (see <c>FakeAuditLogWriter</c>'s recording tests for the catalog).</summary>
    public string? Action { get; init; }

    /// <summary>Exact-match filter on
    /// <see cref="Storporate.SharedKernel.Entities.AuditLogEntry.ResourceType"/>.</summary>
    public string? ResourceType { get; init; }

    /// <summary>Exact-match filter on
    /// <see cref="Storporate.SharedKernel.Entities.AuditLogEntry.AccountId"/>.</summary>
    public Guid? AccountId { get; init; }

    /// <summary>Exact-match filter on
    /// <see cref="Storporate.SharedKernel.Entities.AuditLogEntry.ActorUserId"/>.</summary>
    public Guid? ActorUserId { get; init; }

    /// <summary>Inclusive lower bound on
    /// <see cref="Storporate.SharedKernel.Entities.AuditLogEntry.CreatedAt"/> (UTC).</summary>
    public DateTime? FromDate { get; init; }

    /// <summary>Inclusive upper bound on
    /// <see cref="Storporate.SharedKernel.Entities.AuditLogEntry.CreatedAt"/> (UTC).</summary>
    public DateTime? ToDate { get; init; }
}
