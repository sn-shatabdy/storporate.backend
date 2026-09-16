using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Pagination;

namespace Storporate.Modules.SecurityGovernance;

/// <summary>
/// Builds a paginated, filterable, sorted <see cref="PagedResult{AuditLogEntryResponse}"/>
/// over <see cref="AuditLogEntry"/> for the Administrator-only
/// <c>GET /api/security-governance/audit-log</c> endpoint.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why <see cref="AsNoTracking"/>.</b> The endpoint never mutates rows — every
/// caller-visible side effect is the JSON response — so EF Core's change tracker is
/// pure overhead for the read. <c>AsNoTracking()</c> tells the pipeline to skip the
/// identity map, which on a long admin-browse session over a multi-thousand-row
/// audit feed is a real saving.
/// </para>
/// <para>
/// <b>Why a <see cref="SortMap{T}"/> instead of inline <c>OrderBy</c>.</b>
/// Pagination without a stable tie-breaker can return duplicate rows or skip rows
/// across pages because Postgres doesn't guarantee row order without an explicit
/// <c>ORDER BY</c>. The map's mandatory <see cref="SortMap{T}.TieBreakBy"/> enforces
/// a stable secondary sort on <c>SequenceNumber</c>; a primary sort by any other
/// column keeps pagination correct.
/// </para>
/// <para>
/// <b>Why no <see cref="IAccountScoped"/> query filter is applied.</b>
/// <see cref="AuditLogEntry"/> deliberately does not implement
/// <see cref="SharedKernel.Entities.IAccountScoped"/> (see the entity's class remarks) —
/// audit rows legitimately have a <see langword="null"/> <c>AccountId</c> for
/// pre-account events, and the Administrator's whole point is to read across every
/// account. Access control is enforced upstream by the endpoint's
/// <see cref="Storporate.SharedKernel.Authorization.Permissions.SecurityGovernance.ViewAuditLog"/>
/// gate, not by a tenant query filter.
/// </para>
/// </remarks>
public static class ListAuditLogEntriesHandler
{
    /// <summary>
    /// The whitelist of supported <c>sortBy</c> values for this handler. <c>createdAt</c>
    /// is both the default and the tie-breaker, so an unsorted call and a
    /// <c>sortBy=createdAt</c> call produce identical, stable orderings. <c>action</c>
    /// and <c>resourceType</c> are exposed for the admin-page "alphabetical browse"
    /// mode that the frontend will likely want later.
    /// </summary>
    private static readonly SortMap<AuditLogEntry> SortMap = new SortMap<AuditLogEntry>()
        // createdAt is the most common sort — chronological browsing — so it doubles as
        // the default. TieBreakBy(createdAt) means even callers who omit sortBy get a
        // stable, gap-free ordering across pages.
        .Register("createdAt", entry => entry.CreatedAt)
        .Default("createdAt")
        .Map("action", entry => entry.Action)
        .Map("resourceType", entry => entry.ResourceType)
        .TieBreakBy(entry => entry.SequenceNumber);

    public static async Task<PagedResult<AuditLogEntryResponse>> ExecuteAsync(
        ListAuditLogEntriesRequest request,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dbContext);

        var spec = request.ToSpec();

        var query = dbContext.AuditLogEntries.AsNoTracking();

        // Filters fold into a single loop via a small struct array. Each entry is either
        // active (selector + captured value) or null-suppressed (skipped at fold time).
        // Doing this as a loop avoids the six near-identical `if (...) query = query.Where(...)`
        // blocks the previous shape had — and keeps every WHERE term null-safe at the
        // call site so callers can supply any subset of the filters without the resulting
        // query having `WHERE @p IS NULL OR Action = @p` style zero-elimination noise that
        // Postgres can't always optimize away.
        var filters = new (bool Active, Expression<Func<AuditLogEntry, bool>> Predicate)[]
        {
            (Active: !string.IsNullOrEmpty(request.Action),
                Predicate: entry => entry.Action == request.Action),
            (Active: !string.IsNullOrEmpty(request.ResourceType),
                Predicate: entry => entry.ResourceType == request.ResourceType),
            (Active: request.AccountId.HasValue,
                Predicate: entry => entry.AccountId == request.AccountId),
            (Active: request.ActorUserId.HasValue,
                Predicate: entry => entry.ActorUserId == request.ActorUserId),
            // CreatedAt is stored as UTC (timestamptz). Normalize the inbound filter to
            // Utc so a caller passing a DateTimeKind.Local value gets compared against the
            // column in the same reference frame; DateTimeKind.Unspecified is treated as
            // already-UTC, matching AuditLogHash.Compute's "force Kind=Utc" discipline.
            (Active: request.FromDate.HasValue,
                Predicate: entry => entry.CreatedAt >= NormalizeFilterTimestamp(request.FromDate!.Value)),
            (Active: request.ToDate.HasValue,
                Predicate: entry => entry.CreatedAt <= NormalizeFilterTimestamp(request.ToDate!.Value)),
        };

        foreach (var filter in filters)
        {
            if (filter.Active)
            {
                query = query.Where(filter.Predicate);
            }
        }

        var ordered = SortMap.TryApply(query, spec);
        if (ordered is null)
        {
            // Non-null + unrecognized sortBy: surface as a 400, not a 500. The supported
            // list includes every registered key (not just the default) so the caller can
            // self-correct without consulting docs.
            throw new UnknownSortKeyException(spec.SortBy!, SortMap.Keys);
        }

        // Count before projecting: CountAsync against the DTO projection becomes
        // SELECT COUNT(*) FROM (SELECT 11-fields FROM ... WHERE ...) sub, which on a
        // multi-thousand-row filtered audit feed is expensive. Counting the entity
        // source first produces a plain SELECT COUNT(*) FROM ... WHERE ... — the same
        // query EF Core would have issued anyway, just without the projection wrapper.
        var totalCount = await ordered.CountAsync(cancellationToken).ConfigureAwait(false);

        // No rows means no need to spin up the projection + Skip/Take: the materialized
        // Items list is empty either way, and Postgres' OFFSET against an empty set
        // would do nothing useful but would still cost a planner roundtrip.
        if (totalCount == 0)
        {
            return new PagedResult<AuditLogEntryResponse>(
                Items: Array.Empty<AuditLogEntryResponse>(),
                PageNumber: spec.PageNumber,
                PageSize: spec.PageSize,
                TotalCount: 0);
        }

        // Hash / PreviousHash never leave the DB, as the Integrity Layer story requires.
        var items = await ordered
            .Select(entry => new AuditLogEntryResponse(
                entry.Id,
                entry.SequenceNumber,
                entry.AccountId,
                entry.ActorUserId,
                entry.Action,
                entry.ResourceType,
                entry.ResourceId,
                entry.IpAddress,
                entry.UserAgent,
                entry.MetadataJson,
                entry.CreatedAt))
            .Skip(spec.Skip)
            .Take(spec.PageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new PagedResult<AuditLogEntryResponse>(
            Items: items,
            PageNumber: spec.PageNumber,
            PageSize: spec.PageSize,
            TotalCount: totalCount);
    }

    /// <summary>
    /// Coerces an inbound filter timestamp into a UTC <see cref="DateTime"/> for safe
    /// comparison against the <see cref="AuditLogEntry.CreatedAt"/> <c>timestamptz</c>
    /// column. <see cref="DateTimeKind.Local"/> values are converted to UTC;
    /// <see cref="DateTimeKind.Unspecified"/> values are tagged as UTC (mirroring the
    /// writer's <see cref="Storporate.SharedKernel.Auditing.AuditLogHash.TruncateToMicroseconds"/>
    /// behavior, which forces Kind=Utc on every hash input).
    /// </summary>
    private static DateTime NormalizeFilterTimestamp(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Utc => value,
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
