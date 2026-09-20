using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.DiscoveryHiring.Exceptions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.DiscoveryHiring.TalentSearch;

/// <summary>
/// Creates a new <see cref="TalentSearchRequest"/> for the calling
/// Organization and enqueues a paired <see cref="Job"/> of type
/// <see cref="DiscoveryJobTypes.SearchTalent"/>. Both writes commit in a
/// single <c>SaveChangesAsync</c> so a partial commit (request without
/// its job, or job without its request) is impossible.
/// </summary>
/// <remarks>
/// <para>
/// <b>Busy policy.</b> If the caller already has a
/// <see cref="TalentSearchRequest"/> with
/// <see cref="TalentSearchStatuses.Pending"/>, the handler throws
/// <see cref="TalentSearchBusyException"/> and writes nothing — the
/// global handler maps it to <c>409 talent_search_busy</c>. The check
/// runs under the EF Core global query filter on
/// <see cref="IAccountScoped"/> so a second Organization cannot see
/// (and thus cannot be wrongly blocked by) this Organization's
/// pending row.
/// </para>
/// <para>
/// <b>Audit.</b> After save, the handler writes a
/// <c>talent_search_requested</c> audit row with metadata
/// <c>{queryLength:int, queryHash:string}</c>. The raw query text
/// NEVER enters the audit metadata (plan-mandated rule — only the
/// 64-char lowercase hex SHA-256 of the trimmed query and its length).
/// </para>
/// <para>
/// <b>Cross-account callers.</b> The handler resolves
/// <see cref="IAccountContext.AccountId"/> the same way
/// <see cref="Portfolio.CreatePortfolioItemHandler"/> does — explicit
/// override (cross-account admin) wins, self-service falls back to
/// the JWT subject. The save-time
/// <see cref="RowLevelSecurityInterceptor"/> re-validates the same
/// value, so a manipulated client-supplied account id would fail
/// loudly at the DB rather than silently leak.
/// </para>
/// </remarks>
public static class CreateTalentSearchHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Outcome returned to the endpoint. <see cref="Accepted"/>
    /// is <see langword="false"/> only when the handler decided to
    /// refuse the request without throwing; the current busy path
    /// throws rather than returning <see langword="false"/>, so this
    /// is currently always <see langword="true"/> on the success
    /// path.</summary>
    public sealed record CreateOutcome(bool Accepted, Guid SearchId);

    public static async Task<CreateOutcome> ExecuteAsync(
        CreateTalentSearchRequest request,
        WriteDbContext dbContext,
        IAccountContext accountContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(accountContext);
        ArgumentNullException.ThrowIfNull(auditLogWriter);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (!accountContext.UserId.HasValue)
        {
            // Unreachable when the endpoint is registered with
            // .RequireAuthorization() — the JWT bearer middleware
            // populates IAccountContext from the principal before the
            // handler runs. Throw rather than default so a misconfigured
            // pipeline fails loudly, not silently.
            throw new InvalidOperationException(
                "Cannot create a talent search without an ambient account context.");
        }

        var accountId = accountContext.AccountId ?? accountContext.UserId.Value;

        // The validator has already trimmed and length-checked the
        // query, so the ! on .Query is safe here. Trim once and reuse —
        // the audit hash is over the trimmed input.
        var trimmedQuery = request.Query!.Trim();
        var queryLength = trimmedQuery.Length;
        var queryHash = ComputeQueryHash(trimmedQuery);

        // Busy check. The AnyAsync runs under the global query filter
        // on IAccountScoped — a competing Organization's pending row is
        // never visible to this caller.
        var hasPending = await dbContext.TalentSearchRequests
            .AsNoTracking()
            .AnyAsync(r => r.Status == TalentSearchStatuses.Pending, cancellationToken)
            .ConfigureAwait(false);
        if (hasPending)
        {
            throw new TalentSearchBusyException(accountId);
        }

        var now = timeProvider.GetUtcNow();

        var search = new TalentSearchRequest
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            QueryText = trimmedQuery,
            Status = TalentSearchStatuses.Pending,
            ResultJson = null,
            ErrorCode = null,
            CreatedAt = now,
        };
        dbContext.TalentSearchRequests.Add(search);

        var job = new Job
        {
            Id = Guid.NewGuid(),
            Type = DiscoveryJobTypes.SearchTalent,
            Status = JobStatus.Pending,
            AttemptCount = 0,
            PayloadJson = JsonSerializer.Serialize(
                new SearchTalentPayload(search.Id), JsonOptions),
            AccountId = accountId,
            CreatedAt = now.UtcDateTime,
            UpdatedAt = now.UtcDateTime,
        };
        dbContext.Jobs.Add(job);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Audit metadata: queryLength + queryHash — NEVER the raw
        // query text. Plan rule (Section 6, "Audit"): the raw query
        // must never enter the audit chain.
        var auditMetadata = JsonSerializer.Serialize(
            new
            {
                queryLength,
                queryHash,
            },
            JsonOptions);
        await auditLogWriter.WriteAsync(
            action: "talent_search_requested",
            resourceType: "TalentSearch",
            resourceId: search.Id.ToString(),
            metadataJson: auditMetadata,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new CreateOutcome(Accepted: true, SearchId: search.Id);
    }

    /// <summary>Lowercase 64-char hex SHA-256 of the (already-trimmed)
    /// query, computed once at request time and stored in audit
    /// metadata. Pure helper — easy to unit-test.</summary>
    internal static string ComputeQueryHash(string trimmedQuery)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(trimmedQuery), hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
