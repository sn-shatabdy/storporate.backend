using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.DiscoveryHiring.Outreach;

/// <summary>
/// Employer shortlist (STOR-68): save, list and remove candidates. Everything is scoped to the
/// caller's organization account. The candidate is addressed by <c>TalentIndexEntry.Id</c>; the
/// real student account is resolved server-side and never leaves this class. Display data is read
/// only from the non-tenant talent index (employers cannot read student-private tables).
/// </summary>
public static class ShortlistHandler
{
    public enum Failure
    {
        None,
        CandidateNotFound,
    }

    public readonly record struct Result<T>(T? Value, Failure Failure, bool Created = false)
        where T : class
    {
        public bool IsSuccess => Failure == Failure.None;
    }

    public static async Task<Result<ShortlistEntryResponse>> AddAsync(
        Guid candidateId,
        Guid organizationId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var candidate = await dbContext.TalentIndexEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == candidateId, cancellationToken)
            .ConfigureAwait(false);
        if (candidate is null)
        {
            return new(null, Failure.CandidateNotFound);
        }

        var existing = await FindAsync(dbContext, organizationId, candidate.StudentAccountId, cancellationToken)
            .ConfigureAwait(false);
        var created = false;
        if (existing is null)
        {
            existing = new ShortlistEntry
            {
                Id = Guid.NewGuid(),
                OrganizationAccountId = organizationId,
                StudentAccountId = candidate.StudentAccountId,
                CandidateId = candidate.Id,
                CreatedAt = timeProvider.GetUtcNow(),
            };
            dbContext.ShortlistEntries.Add(existing);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                created = true;
            }
            catch (DbUpdateException)
            {
                dbContext.Entry(existing).State = EntityState.Detached;
                existing = await FindAsync(dbContext, organizationId, candidate.StudentAccountId, cancellationToken)
                    .ConfigureAwait(false);
                if (existing is null)
                {
                    throw;
                }
            }
        }

        if (created)
        {
            await auditLogWriter.WriteAsync(
                "shortlist_added",
                "ShortlistEntry",
                existing.Id.ToString(),
                JsonSerializer.Serialize(new { candidateId = candidate.Id }, OutreachMapping.JsonOptions),
                cancellationToken).ConfigureAwait(false);
        }

        var conversation = await dbContext.OutreachConversations
            .AsNoTracking()
            .Where(c => c.OrganizationAccountId == organizationId && c.StudentAccountId == candidate.StudentAccountId)
            .Select(c => new ShortlistConversationResponse(c.Id, c.Status))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new(ToResponse(existing, ToDisplay(candidate), conversation), Failure.None, created);
    }

    public static async Task<ShortlistListResponse> ListAsync(
        Guid organizationId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var entries = await dbContext.ShortlistEntries
            .AsNoTracking()
            .Where(e => e.OrganizationAccountId == organizationId)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        if (entries.Count == 0)
        {
            return new ShortlistListResponse([]);
        }

        var studentIds = entries.Select(e => e.StudentAccountId).ToList();
        var candidates = (await dbContext.TalentIndexEntries
                .AsNoTracking()
                .Where(t => studentIds.Contains(t.StudentAccountId))
                .Select(t => new CandidateDisplay(
                    t.Id, t.StudentAccountId, t.DisplayName, t.Headline, t.University, t.FieldOfStudy, t.StudyYear))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .GroupBy(t => t.StudentAccountId)
            .ToDictionary(g => g.Key, g => g.First());
        var conversations = (await dbContext.OutreachConversations
                .AsNoTracking()
                .Where(c => c.OrganizationAccountId == organizationId && studentIds.Contains(c.StudentAccountId))
                .Select(c => new { c.StudentAccountId, c.Id, c.Status })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false))
            .ToDictionary(c => c.StudentAccountId, c => new ShortlistConversationResponse(c.Id, c.Status));

        return new ShortlistListResponse(entries
            .Select(e => ToResponse(
                e,
                candidates.GetValueOrDefault(e.StudentAccountId),
                conversations.GetValueOrDefault(e.StudentAccountId)))
            .ToList());
    }

    public static async Task RemoveAsync(
        Guid candidateId,
        Guid organizationId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        CancellationToken cancellationToken)
    {
        // The candidate id is either the stored one or the id of the student's current index entry.
        var currentStudentId = await dbContext.TalentIndexEntries
            .AsNoTracking()
            .Where(t => t.Id == candidateId)
            .Select(t => (Guid?)t.StudentAccountId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        var entry = await dbContext.ShortlistEntries
            .FirstOrDefaultAsync(
                e => e.OrganizationAccountId == organizationId
                    && (e.CandidateId == candidateId || e.StudentAccountId == currentStudentId),
                cancellationToken)
            .ConfigureAwait(false);
        if (entry is null)
        {
            return;
        }

        dbContext.ShortlistEntries.Remove(entry);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await auditLogWriter.WriteAsync(
            "shortlist_removed",
            "ShortlistEntry",
            entry.Id.ToString(),
            JsonSerializer.Serialize(new { candidateId }, OutreachMapping.JsonOptions),
            cancellationToken).ConfigureAwait(false);
    }

    private static Task<ShortlistEntry?> FindAsync(
        WriteDbContext dbContext,
        Guid organizationId,
        Guid studentId,
        CancellationToken cancellationToken) =>
        dbContext.ShortlistEntries
            .AsNoTracking()
            .FirstOrDefaultAsync(
                e => e.OrganizationAccountId == organizationId && e.StudentAccountId == studentId,
                cancellationToken);

    // Display-only projection of the talent index entry; the student account id is only used as a lookup key.
    private sealed record CandidateDisplay(
        Guid Id,
        Guid StudentAccountId,
        string DisplayName,
        string? Headline,
        string? University,
        string? FieldOfStudy,
        int? StudyYear);

    private static CandidateDisplay ToDisplay(TalentIndexEntry t) =>
        new(t.Id, t.StudentAccountId, t.DisplayName, t.Headline, t.University, t.FieldOfStudy, t.StudyYear);

    private static ShortlistEntryResponse ToResponse(
        ShortlistEntry entry,
        CandidateDisplay? candidate,
        ShortlistConversationResponse? conversation) =>
        candidate is null
            ? new ShortlistEntryResponse(
                entry.CandidateId, null, null, null, null, null, false, entry.CreatedAt, conversation)
            : new ShortlistEntryResponse(
                candidate.Id,
                candidate.DisplayName,
                candidate.Headline,
                candidate.University,
                candidate.FieldOfStudy,
                candidate.StudyYear,
                true,
                entry.CreatedAt,
                conversation);
}
