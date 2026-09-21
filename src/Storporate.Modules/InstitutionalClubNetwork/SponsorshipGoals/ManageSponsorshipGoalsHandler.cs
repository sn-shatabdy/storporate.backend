using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.InstitutionalClubNetwork.SponsorshipGoals;

/// <summary>
/// Company-side goal set operations (STOR-70). Every operation is scoped to
/// <see cref="SponsorshipGoalSet.OwnerAccountId"/> == caller account, so one company can never
/// read or affect another company's sets (foreign ids behave like unknown ids). Audit rows carry
/// ids and the status only, never goal text, and are written after SaveChanges.
/// </summary>
public static class ManageSponsorshipGoalsHandler
{
    public static async Task<SponsorshipGoalSetListResponse> ListOwnAsync(
        Guid accountId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var rows = await dbContext.SponsorshipGoalSets
            .AsNoTracking()
            .Where(s => s.OwnerAccountId == accountId)
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return new SponsorshipGoalSetListResponse(rows.Select(ToResponse).ToList());
    }

    public static async Task<SponsorshipGoalSetResponse?> GetOwnAsync(
        Guid id,
        Guid accountId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var set = await dbContext.SponsorshipGoalSets
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.OwnerAccountId == accountId, cancellationToken)
            .ConfigureAwait(false);
        return set is null ? null : ToResponse(set);
    }

    public static async Task<SponsorshipGoalSetResponse> CreateAsync(
        SaveSponsorshipGoalRequest request,
        Guid accountId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var set = new SponsorshipGoalSet
        {
            Id = Guid.NewGuid(),
            OwnerAccountId = accountId,
            Name = string.Empty,
            CompanyName = string.Empty,
            Objectives = "[]",
            AudienceFieldsOfStudy = "[]",
            AudienceYears = "[]",
            AudienceCities = "[]",
            AudienceUniversities = "[]",
            EventKinds = "[]",
            Status = SponsorshipGoalStatuses.Active,
            CreatedAt = now,
            UpdatedAt = now,
        };
        Apply(set, request);
        dbContext.SponsorshipGoalSets.Add(set);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await WriteAuditAsync(auditLogWriter, "sponsorship_goal_created", set.Id, null, cancellationToken).ConfigureAwait(false);
        return ToResponse(set);
    }

    /// <summary>Returns null when the caller owns no set with that id. The status is kept.</summary>
    public static async Task<SponsorshipGoalSetResponse?> UpdateAsync(
        Guid id,
        SaveSponsorshipGoalRequest request,
        Guid accountId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var set = await FindOwnAsync(id, accountId, dbContext, cancellationToken).ConfigureAwait(false);
        if (set is null)
        {
            return null;
        }

        Apply(set, request);
        set.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await WriteAuditAsync(auditLogWriter, "sponsorship_goal_updated", set.Id, null, cancellationToken).ConfigureAwait(false);
        return ToResponse(set);
    }

    /// <summary>Returns null when the caller owns no set with that id. Setting the current status is a no-op with no audit row.</summary>
    public static async Task<SponsorshipGoalSetResponse?> SetStatusAsync(
        Guid id,
        string status,
        Guid accountId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var set = await FindOwnAsync(id, accountId, dbContext, cancellationToken).ConfigureAwait(false);
        if (set is null)
        {
            return null;
        }

        if (set.Status == status)
        {
            return ToResponse(set);
        }

        set.Status = status;
        set.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await WriteAuditAsync(auditLogWriter, "sponsorship_goal_status_changed", set.Id, status, cancellationToken).ConfigureAwait(false);
        return ToResponse(set);
    }

    /// <summary>Hard-deletes the caller's own set. Returns false when the caller owns no set with that id.</summary>
    public static async Task<bool> DeleteAsync(
        Guid id,
        Guid accountId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        CancellationToken cancellationToken)
    {
        var set = await FindOwnAsync(id, accountId, dbContext, cancellationToken).ConfigureAwait(false);
        if (set is null)
        {
            return false;
        }

        dbContext.SponsorshipGoalSets.Remove(set);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await WriteAuditAsync(auditLogWriter, "sponsorship_goal_deleted", id, null, cancellationToken).ConfigureAwait(false);
        return true;
    }

    internal static SponsorshipGoalSetResponse ToResponse(SponsorshipGoalSet s) => new(
        s.Id,
        s.Name,
        s.CompanyName,
        SponsorshipGoalOptions.Deserialize<List<string>>(s.Objectives, []),
        ToAudience(s),
        SponsorshipGoalOptions.Deserialize<List<string>>(s.EventKinds, []),
        new SponsorshipOwnBudgetResponse(s.BudgetMin, s.BudgetMax, s.ShowBudget),
        s.Notes,
        s.Status,
        s.CreatedAt,
        s.UpdatedAt);

    internal static SponsorshipAudienceResponse ToAudience(SponsorshipGoalSet s) => new(
        SponsorshipGoalOptions.Deserialize<List<string>>(s.AudienceFieldsOfStudy, []),
        SponsorshipGoalOptions.Deserialize<List<int>>(s.AudienceYears, []),
        SponsorshipGoalOptions.Deserialize<List<string>>(s.AudienceCities, []),
        SponsorshipGoalOptions.Deserialize<List<string>>(s.AudienceUniversities, []));

    private static Task<SponsorshipGoalSet?> FindOwnAsync(
        Guid id,
        Guid accountId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken) =>
        dbContext.SponsorshipGoalSets
            .FirstOrDefaultAsync(s => s.Id == id && s.OwnerAccountId == accountId, cancellationToken);

    private static void Apply(SponsorshipGoalSet set, SaveSponsorshipGoalRequest request)
    {
        var audience = request.Audience!;
        set.Name = request.Name!.Trim();
        set.CompanyName = request.CompanyName!.Trim();
        set.Objectives = SponsorshipGoalOptions.Serialize(
            SponsorshipGoalOptions.NormalizeFromList(request.Objectives!, SponsorshipGoalOptions.Objectives));
        set.AudienceFieldsOfStudy = SponsorshipGoalOptions.Serialize(
            SponsorshipGoalOptions.NormalizeNames(audience.FieldsOfStudy ?? []));
        set.AudienceYears = SponsorshipGoalOptions.Serialize(
            SponsorshipGoalOptions.NormalizeYears(audience.Years ?? []));
        set.AudienceCities = SponsorshipGoalOptions.Serialize(
            SponsorshipGoalOptions.NormalizeNames(audience.Cities ?? []));
        set.AudienceUniversities = SponsorshipGoalOptions.Serialize(
            SponsorshipGoalOptions.NormalizeNames(audience.Universities ?? []));
        set.EventKinds = SponsorshipGoalOptions.Serialize(
            SponsorshipGoalOptions.NormalizeFromList(request.EventKinds!, SponsorshipGoalOptions.EventKinds));
        set.BudgetMin = request.Budget?.Min;
        set.BudgetMax = request.Budget?.Max;
        set.ShowBudget = request.Budget?.VisibleToClubs ?? false;
        var notes = request.Notes?.Trim();
        set.Notes = string.IsNullOrEmpty(notes) ? null : notes;
    }

    private static Task WriteAuditAsync(
        IAuditLogWriter auditLogWriter,
        string action,
        Guid goalId,
        string? status,
        CancellationToken cancellationToken)
    {
        object metadata = status is null
            ? new { goalId }
            : new { goalId, status };
        return auditLogWriter.WriteAsync(
            action,
            "SponsorshipGoalSet",
            goalId.ToString(),
            SponsorshipGoalOptions.Serialize(metadata),
            cancellationToken);
    }
}
