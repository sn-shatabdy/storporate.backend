using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.InstitutionalClubNetwork.Exceptions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.InstitutionalClubNetwork.ClubProfiles;

/// <summary>
/// Club-side profile operations (STOR-69). Every operation is scoped to
/// <see cref="ClubProfile.OwnerAccountId"/> == caller account, so one club can never read or
/// affect another club's profile. Audit rows carry ids only, never profile text, and are
/// written after SaveChanges.
/// </summary>
public static class ManageClubProfileHandler
{
    public static async Task<ClubProfileResponse?> GetOwnAsync(
        Guid accountId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var profile = await dbContext.ClubProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.OwnerAccountId == accountId, cancellationToken)
            .ConfigureAwait(false);
        return profile is null ? null : ToResponse(profile);
    }

    /// <summary>Creates a Draft on first save; later saves keep the current status.</summary>
    public static async Task<ClubProfileResponse> SaveAsync(
        SaveClubProfileRequest request,
        Guid accountId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var profile = await dbContext.ClubProfiles
            .FirstOrDefaultAsync(p => p.OwnerAccountId == accountId, cancellationToken)
            .ConfigureAwait(false);

        var fields = ClubProfileOptions.NormalizeNames(request.Audience?.FieldsOfStudy ?? []);
        var years = ClubProfileOptions.NormalizeYears(request.Audience?.Years ?? []);
        var events = NormalizeEvents(request.Events ?? []);

        if (profile is null)
        {
            profile = new ClubProfile
            {
                Id = Guid.NewGuid(),
                OwnerAccountId = accountId,
                Name = string.Empty,
                About = string.Empty,
                University = string.Empty,
                AudienceFieldsOfStudy = "[]",
                AudienceYears = "[]",
                EventsJson = "[]",
                Status = ClubProfileStatuses.Draft,
                CreatedAt = now,
                UpdatedAt = now,
            };
            dbContext.ClubProfiles.Add(profile);
        }

        profile.Name = request.Name!.Trim();
        profile.Tagline = NormalizeOptional(request.Tagline);
        profile.About = request.About!.Trim();
        profile.University = request.University!.Trim();
        profile.City = NormalizeOptional(request.City);
        profile.FoundedYear = request.FoundedYear;
        profile.MemberCount = request.MemberCount!.Value;
        profile.AudienceFieldsOfStudy = JsonSerializer.Serialize(fields, ClubProfileOptions.Json);
        profile.AudienceYears = JsonSerializer.Serialize(years, ClubProfileOptions.Json);
        profile.EventsJson = JsonSerializer.Serialize(events, ClubProfileOptions.Json);
        profile.UpdatedAt = now;

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsUniqueViolationOnOwnerIndex(ex))
        {
            // Two concurrent first PUTs from the same club account both pass the
            // null-check above; the second one's INSERT collides with the
            // IX_ClubProfiles_OwnerAccountId unique index. The pre-check handles
            // sequential re-saves; this catch handles the race window where both
            // callers have already passed the null-check. Translate to the same
            // conflict the xmin path throws so the global handler maps it to
            // 409 club_profile_conflict.
            throw new ClubProfileConflictException();
        }
        catch (DbUpdateConcurrencyException)
        {
            // Existing-profile update path: the xmin concurrency token mapped on
            // ClubProfile fired because another writer committed between our
            // load and our SaveChanges. Map to the same exception so the global
            // handler responds with 409 club_profile_conflict — the operator
            // gets a single error code for both race types.
            throw new ClubProfileConflictException();
        }

        await WriteAuditAsync(auditLogWriter, "club_profile_updated", profile.Id, cancellationToken).ConfigureAwait(false);
        return ToResponse(profile);
    }

    /// <summary>Returns null when the club has no profile yet. Throws <see cref="ClubProfileIncompleteException"/> when required parts are missing.</summary>
    public static async Task<ClubProfileResponse?> PublishAsync(
        Guid accountId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var profile = await dbContext.ClubProfiles
            .FirstOrDefaultAsync(p => p.OwnerAccountId == accountId, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return null;
        }

        if (profile.Status == ClubProfileStatuses.Published)
        {
            return ToResponse(profile);
        }

        var missing = MissingParts(profile);
        if (missing.Count > 0)
        {
            throw new ClubProfileIncompleteException(
                $"Add the following before publishing: {string.Join(", ", missing)}.");
        }

        var now = timeProvider.GetUtcNow();
        profile.Status = ClubProfileStatuses.Published;
        profile.PublishedAt = now;
        profile.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await WriteAuditAsync(auditLogWriter, "club_profile_published", profile.Id, cancellationToken).ConfigureAwait(false);
        return ToResponse(profile);
    }

    /// <summary>Returns null when the club has no profile yet. A Draft stays Draft with no audit row.</summary>
    public static async Task<ClubProfileResponse?> UnpublishAsync(
        Guid accountId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var profile = await dbContext.ClubProfiles
            .FirstOrDefaultAsync(p => p.OwnerAccountId == accountId, cancellationToken)
            .ConfigureAwait(false);
        if (profile is null)
        {
            return null;
        }

        if (profile.Status == ClubProfileStatuses.Draft)
        {
            return ToResponse(profile);
        }

        profile.Status = ClubProfileStatuses.Draft;
        profile.PublishedAt = null;
        profile.UpdatedAt = timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await WriteAuditAsync(auditLogWriter, "club_profile_unpublished", profile.Id, cancellationToken).ConfigureAwait(false);
        return ToResponse(profile);
    }

    internal static ClubProfileResponse ToResponse(ClubProfile p) => new(
        p.Id,
        p.Name,
        p.Tagline,
        p.About,
        p.University,
        p.City,
        p.FoundedYear,
        p.MemberCount,
        new ClubAudienceResponse(
            ClubProfileOptions.Deserialize<List<string>>(p.AudienceFieldsOfStudy, []),
            ClubProfileOptions.Deserialize<List<int>>(p.AudienceYears, [])),
        ClubProfileOptions.Deserialize<List<ClubEvent>>(p.EventsJson, []),
        p.Status,
        p.UpdatedAt,
        p.PublishedAt);

    private static List<string> MissingParts(ClubProfile p)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(p.Name))
        {
            missing.Add("club name");
        }

        if (string.IsNullOrWhiteSpace(p.About))
        {
            missing.Add("about");
        }

        if (string.IsNullOrWhiteSpace(p.University))
        {
            missing.Add("university");
        }

        if (p.MemberCount < 0)
        {
            missing.Add("member count");
        }

        if (ClubProfileOptions.Deserialize<List<string>>(p.AudienceFieldsOfStudy, []).Count == 0)
        {
            missing.Add("at least one field of study");
        }

        if (ClubProfileOptions.Deserialize<List<int>>(p.AudienceYears, []).Count == 0)
        {
            missing.Add("at least one study year");
        }

        return missing;
    }

    private static List<ClubEvent> NormalizeEvents(IEnumerable<ClubEventRequest> requests)
    {
        var usedIds = new HashSet<Guid>();
        var result = new List<ClubEvent>();
        foreach (var e in requests)
        {
            var id = e.Id is { } given && given != Guid.Empty && usedIds.Add(given) ? given : NewUnusedId(usedIds);
            var supportNeeds = ClubProfileOptions.SupportNeeds
                .Where(need => (e.SupportNeeds ?? []).Contains(need, StringComparer.Ordinal))
                .ToList();
            result.Add(new ClubEvent(
                id,
                e.Title!.Trim(),
                NormalizeOptional(e.Description),
                e.TypicalAttendance!.Value,
                e.Frequency!,
                supportNeeds));
        }

        return result;
    }

    private static Guid NewUnusedId(HashSet<Guid> used)
    {
        var id = Guid.NewGuid();
        while (!used.Add(id))
        {
            id = Guid.NewGuid();
        }

        return id;
    }

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    /// <summary>Walks the <see cref="DbUpdateException"/> cause chain and
    /// returns <see langword="true"/> only when the inner Postgres error is a
    /// <c>23505 unique_violation</c> raised by the unique index
    /// <c>IX_ClubProfiles_OwnerAccountId</c>. Any other 23505 (a future unique
    /// index on a different column) or any other SqlState bubbles up so the
    /// global exception handler can map it to its own status. Same shape as
    /// <c>CreateTalentSearchHandler.IsUniqueViolationOnPendingIndex</c> in the
    /// DiscoveryHiring module.</summary>
    private static bool IsUniqueViolationOnOwnerIndex(DbUpdateException ex)
    {
        for (var current = ex.InnerException; current is not null; current = current.InnerException)
        {
            if (current is PostgresException pg
                && pg.SqlState == "23505"
                && string.Equals(
                    pg.ConstraintName,
                    "IX_ClubProfiles_OwnerAccountId",
                    StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static Task WriteAuditAsync(
        IAuditLogWriter auditLogWriter,
        string action,
        Guid profileId,
        CancellationToken cancellationToken) =>
        auditLogWriter.WriteAsync(
            action,
            "ClubProfile",
            profileId.ToString(),
            JsonSerializer.Serialize(new { clubProfileId = profileId }, ClubProfileOptions.Json),
            cancellationToken);
}
