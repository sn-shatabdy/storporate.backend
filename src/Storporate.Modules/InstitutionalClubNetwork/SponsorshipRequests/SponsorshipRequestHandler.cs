using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.InstitutionalClubNetwork.SponsorshipGoals;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.InstitutionalClubNetwork.SponsorshipRequests;

/// <summary>
/// Sponsorship request operations for both sides (STOR-72). The club side is scoped by
/// <see cref="SponsorshipRequest.ClubOwnerAccountId"/>, the company side by
/// <see cref="SponsorshipRequest.CompanyOwnerAccountId"/>; a request of another account behaves like
/// an unknown id. Failures are returned inline as error codes. Audit rows carry ids, the side and the
/// status only, never any text, and are written after SaveChanges.
/// </summary>
public static class SponsorshipRequestHandler
{
    public const int MaxListItems = 100;

    private const string ResourceType = "SponsorshipRequest";

    public static async Task<SponsorshipRequestOutcome<SponsorshipRequestDetail>> CreateAsync(
        CreateSponsorshipRequestRequest request,
        Guid accountId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var profile = await dbContext.ClubProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.OwnerAccountId == accountId, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return Fail<SponsorshipRequestDetail>(SponsorshipRequestErrors.ClubProfileNotFound);
        }

        if (profile.Status != ClubProfileStatuses.Published)
        {
            return Fail<SponsorshipRequestDetail>(SponsorshipRequestErrors.ClubProfileNotPublished);
        }

        var goalId = request.GoalId!.Value;
        var goalSet = await dbContext.SponsorshipGoalSets
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == goalId && s.Status == SponsorshipGoalStatuses.Active, cancellationToken)
            .ConfigureAwait(false);
        if (goalSet is null)
        {
            return Fail<SponsorshipRequestDetail>(SponsorshipRequestErrors.GoalNotFound);
        }

        var title = request.EventTitle!.Trim();
        var lowerTitle = title.ToLower();
        var duplicate = await dbContext.SponsorshipRequests
            .AsNoTracking()
            .AnyAsync(
                r => r.ClubOwnerAccountId == accountId
                    && r.GoalSetId == goalId
                    && r.EventTitle.ToLower() == lowerTitle
                    && r.Status != SponsorshipRequestStatuses.Declined
                    && r.Status != SponsorshipRequestStatuses.Completed,
                cancellationToken)
            .ConfigureAwait(false);
        if (duplicate)
        {
            return Fail<SponsorshipRequestDetail>(SponsorshipRequestErrors.Duplicate);
        }

        var now = timeProvider.GetUtcNow();
        var entity = new SponsorshipRequest
        {
            Id = Guid.NewGuid(),
            ClubOwnerAccountId = accountId,
            CompanyOwnerAccountId = goalSet.OwnerAccountId,
            ClubProfileId = profile.Id,
            GoalSetId = goalSet.Id,
            ClubName = profile.Name,
            University = profile.University,
            CompanyName = goalSet.CompanyName,
            GoalName = goalSet.Name,
            EventTitle = title,
            EventDate = request.EventDate,
            EventDescription = request.EventDescription!.Trim(),
            Ask = request.Ask!.Trim(),
            AmountRequested = request.AmountRequested,
            Offer = request.Offer!.Trim(),
            Status = SponsorshipRequestStatuses.Sent,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.SponsorshipRequests.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await WriteAuditAsync(auditLogWriter, "sponsorship_request_sent", entity.Id, new { requestId = entity.Id, goalId }, cancellationToken)
            .ConfigureAwait(false);
        return Ok(ToDetail(entity, SponsorshipRequestSides.Club));
    }

    public static async Task<SponsorshipRequestOutcome<SponsorshipRequestListResponse>> ListAsync(
        string side,
        Guid accountId,
        string? status,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var statusFilter = string.IsNullOrWhiteSpace(status) ? null : status;
        if (statusFilter is not null && !SponsorshipRequestStatuses.All.Contains(statusFilter))
        {
            return Fail<SponsorshipRequestListResponse>(SponsorshipRequestErrors.StatusInvalid);
        }

        var isClub = side == SponsorshipRequestSides.Club;
        var all = dbContext.SponsorshipRequests.AsNoTracking();
        var query = isClub
            ? all.Where(r => r.ClubOwnerAccountId == accountId)
            : all.Where(r => r.CompanyOwnerAccountId == accountId);
        if (statusFilter is not null)
        {
            query = query.Where(r => r.Status == statusFilter);
        }

        var rows = await query
            .OrderByDescending(r => r.UpdatedAt)
            .ThenByDescending(r => r.Id)
            .Take(MaxListItems)
            .Select(r => new
            {
                r.Id,
                r.Status,
                r.EventTitle,
                r.EventDate,
                r.ClubName,
                r.CompanyName,
                r.GoalName,
                r.AmountRequested,
                r.CreatedAt,
                r.UpdatedAt,
                MessageCount = r.Messages.Count,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var items = rows
            .Select(r => new SponsorshipRequestSummary(
                r.Id,
                r.Status,
                r.EventTitle,
                r.EventDate,
                isClub ? r.CompanyName : r.ClubName,
                r.GoalName,
                r.AmountRequested,
                r.CreatedAt,
                r.UpdatedAt,
                r.MessageCount))
            .ToList();
        return Ok(new SponsorshipRequestListResponse(items));
    }

    /// <summary>The company opening a Sent request moves it to Viewed; every other open changes nothing.</summary>
    public static async Task<SponsorshipRequestOutcome<SponsorshipRequestDetail>> GetAsync(
        string side,
        Guid id,
        Guid accountId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var entity = await FindAsync(side, id, accountId, dbContext, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return Fail<SponsorshipRequestDetail>(SponsorshipRequestErrors.NotFound);
        }

        if (side == SponsorshipRequestSides.Company && SponsorshipRequestRules.MarksViewed(entity.Status))
        {
            var now = timeProvider.GetUtcNow();
            entity.Status = SponsorshipRequestStatuses.Viewed;
            entity.ViewedAt = now;
            entity.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await WriteAuditAsync(auditLogWriter, "sponsorship_request_viewed", entity.Id, new { requestId = entity.Id }, cancellationToken)
                .ConfigureAwait(false);
        }

        return Ok(ToDetail(entity, side));
    }

    public static async Task<SponsorshipRequestOutcome<SponsorshipRequestDetail>> SendMessageAsync(
        string side,
        Guid id,
        Guid accountId,
        SendSponsorshipMessageRequest request,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var entity = await FindAsync(side, id, accountId, dbContext, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return Fail<SponsorshipRequestDetail>(SponsorshipRequestErrors.NotFound);
        }

        if (!SponsorshipRequestRules.CanPostMessage(entity.Status))
        {
            return Fail<SponsorshipRequestDetail>(SponsorshipRequestErrors.Closed);
        }

        if (entity.Messages.Count >= SponsorshipRequestRules.MaxMessages)
        {
            return Fail<SponsorshipRequestDetail>(SponsorshipRequestErrors.ThreadFull);
        }

        var now = timeProvider.GetUtcNow();
        dbContext.SponsorshipRequestMessages.Add(new SponsorshipRequestMessage
        {
            Id = Guid.NewGuid(),
            RequestId = entity.Id,
            SenderSide = side,
            Body = request.Body!.Trim(),
            CreatedAt = now,
        });

        var nextStatus = SponsorshipRequestRules.StatusAfterMessage(entity.Status);
        var statusChanged = nextStatus != entity.Status;
        entity.Status = nextStatus;
        entity.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await WriteAuditAsync(auditLogWriter, "sponsorship_request_message_sent", entity.Id, new { requestId = entity.Id, side }, cancellationToken)
            .ConfigureAwait(false);
        if (statusChanged)
        {
            await WriteStatusChangedAsync(auditLogWriter, entity, cancellationToken).ConfigureAwait(false);
        }

        return Ok(ToDetail(entity, side));
    }

    /// <summary>Company accept (<paramref name="accept"/> true) or decline; allowed from Sent, Viewed or InDiscussion.</summary>
    public static async Task<SponsorshipRequestOutcome<SponsorshipRequestDetail>> DecideAsync(
        Guid id,
        Guid accountId,
        bool accept,
        string? note,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var entity = await FindAsync(SponsorshipRequestSides.Company, id, accountId, dbContext, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return Fail<SponsorshipRequestDetail>(SponsorshipRequestErrors.NotFound);
        }

        if (!SponsorshipRequestRules.CanDecide(entity.Status))
        {
            return Fail<SponsorshipRequestDetail>(SponsorshipRequestErrors.InvalidTransition);
        }

        var now = timeProvider.GetUtcNow();
        var trimmed = note?.Trim();
        entity.Status = accept ? SponsorshipRequestStatuses.Agreed : SponsorshipRequestStatuses.Declined;
        entity.DecisionNote = string.IsNullOrEmpty(trimmed) ? null : trimmed;
        entity.DecidedAt = now;
        entity.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await WriteStatusChangedAsync(auditLogWriter, entity, cancellationToken).ConfigureAwait(false);
        return Ok(ToDetail(entity, SponsorshipRequestSides.Company));
    }

    /// <summary>Either side completes an Agreed request and records the outcome.</summary>
    public static async Task<SponsorshipRequestOutcome<SponsorshipRequestDetail>> CompleteAsync(
        string side,
        Guid id,
        Guid accountId,
        CompleteSponsorshipRequestRequest request,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var entity = await FindAsync(side, id, accountId, dbContext, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return Fail<SponsorshipRequestDetail>(SponsorshipRequestErrors.NotFound);
        }

        if (!SponsorshipRequestRules.CanComplete(entity.Status))
        {
            return Fail<SponsorshipRequestDetail>(SponsorshipRequestErrors.InvalidTransition);
        }

        var now = timeProvider.GetUtcNow();
        entity.Status = SponsorshipRequestStatuses.Completed;
        entity.OutcomeNote = request.OutcomeNote!.Trim();
        entity.AgreedAmount = request.AgreedAmount;
        entity.CompletedAt = now;
        entity.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await WriteAuditAsync(auditLogWriter, "sponsorship_request_completed", entity.Id, new { requestId = entity.Id }, cancellationToken)
            .ConfigureAwait(false);
        return Ok(ToDetail(entity, side));
    }

    internal static SponsorshipRequestDetail ToDetail(SponsorshipRequest r, string side) => new(
        r.Id,
        r.Status,
        r.EventTitle,
        r.EventDate,
        r.EventDescription,
        r.Ask,
        r.AmountRequested,
        r.Offer,
        new SponsorshipRequestClubInfo(r.ClubName, r.University),
        new SponsorshipRequestCompanyInfo(r.CompanyName, r.GoalName),
        r.CreatedAt,
        r.UpdatedAt,
        r.ViewedAt,
        r.DecidedAt,
        r.CompletedAt,
        r.DecisionNote,
        r.OutcomeNote is null ? null : new SponsorshipRequestOutcomeInfo(r.OutcomeNote, r.AgreedAmount),
        r.Messages
            .OrderBy(m => m.CreatedAt)
            .ThenBy(m => m.Id)
            .Select(m => new SponsorshipRequestMessageResponse(m.Id, m.SenderSide, m.Body, m.CreatedAt))
            .ToList(),
        SponsorshipRequestRules.AllowedActions(side, r.Status));

    private static Task<SponsorshipRequest?> FindAsync(
        string side,
        Guid id,
        Guid accountId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var query = dbContext.SponsorshipRequests.Include(r => r.Messages).Where(r => r.Id == id);
        return side == SponsorshipRequestSides.Club
            ? query.FirstOrDefaultAsync(r => r.ClubOwnerAccountId == accountId, cancellationToken)
            : query.FirstOrDefaultAsync(r => r.CompanyOwnerAccountId == accountId, cancellationToken);
    }

    private static Task WriteStatusChangedAsync(
        IAuditLogWriter auditLogWriter,
        SponsorshipRequest entity,
        CancellationToken cancellationToken) =>
        WriteAuditAsync(
            auditLogWriter,
            "sponsorship_request_status_changed",
            entity.Id,
            new { requestId = entity.Id, status = entity.Status },
            cancellationToken);

    private static Task WriteAuditAsync(
        IAuditLogWriter auditLogWriter,
        string action,
        Guid requestId,
        object metadata,
        CancellationToken cancellationToken) =>
        auditLogWriter.WriteAsync(
            action,
            ResourceType,
            requestId.ToString(),
            SponsorshipGoalOptions.Serialize(metadata),
            cancellationToken);

    private static SponsorshipRequestOutcome<T> Ok<T>(T value)
        where T : class => SponsorshipRequestOutcome<T>.Ok(value);

    private static SponsorshipRequestOutcome<T> Fail<T>(string errorCode)
        where T : class => SponsorshipRequestOutcome<T>.Fail(errorCode);
}
