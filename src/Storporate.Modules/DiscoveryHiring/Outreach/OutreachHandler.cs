using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.DiscoveryHiring.Exceptions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.DiscoveryHiring.Outreach;

/// <summary>
/// Employer-student outreach (STOR-68), both sides. The conversation state machine lives here:
/// Invited (employer wrote first) to Replied (student answered; both sides then talk freely), and
/// Declined (student's final choice, allowed from Invited or Replied). The employer side is scoped
/// by organization account, the student side by student account; a conversation that belongs to
/// someone else is indistinguishable from a missing one (<see cref="Failure.NotFound"/>).
/// Neither side ever receives the other's account id, e-mail or any other contact detail: the
/// employer sees the student display name snapshot, the student sees the organization name the
/// employer typed. Audit rows hold ids, status and sender role only, never message text.
/// </summary>
public static class OutreachHandler
{
    public enum Failure
    {
        None,
        CandidateNotFound,
        NotFound,
    }

    public readonly record struct Result<T>(T? Value, Failure Failure)
        where T : class
    {
        public bool IsSuccess => Failure == Failure.None;
    }

    // ---------------------------------------------------------------- employer

    public static async Task<Result<ConversationDetailResponse>> InviteAsync(
        InviteCandidateRequest request,
        Guid organizationId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var candidate = await dbContext.TalentIndexEntries
            .AsNoTracking()
            .Where(e => e.Id == request.CandidateId)
            .Select(e => new { e.StudentAccountId, e.DisplayName })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (candidate is null)
        {
            return new(null, Failure.CandidateNotFound);
        }

        var existingStatus = await dbContext.OutreachConversations
            .AsNoTracking()
            .Where(c => c.OrganizationAccountId == organizationId && c.StudentAccountId == candidate.StudentAccountId)
            .Select(c => c.Status)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        ThrowIfExists(existingStatus);

        var now = timeProvider.GetUtcNow();
        var conversation = new OutreachConversation
        {
            Id = Guid.NewGuid(),
            OrganizationAccountId = organizationId,
            StudentAccountId = candidate.StudentAccountId,
            OrganizationName = request.OrganizationName!.Trim(),
            StudentDisplayName = candidate.DisplayName,
            Status = OutreachStatuses.Invited,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var message = NewMessage(conversation.Id, OutreachSenderRoles.Organization, request.Message!, now);
        dbContext.OutreachConversations.Add(conversation);
        dbContext.OutreachMessages.Add(message);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(message).State = EntityState.Detached;
            dbContext.Entry(conversation).State = EntityState.Detached;
            var status = await dbContext.OutreachConversations
                .AsNoTracking()
                .Where(c => c.OrganizationAccountId == organizationId && c.StudentAccountId == candidate.StudentAccountId)
                .Select(c => c.Status)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            // Lost a race with a concurrent invite: the unique (organization, student) index rejected us.
            ThrowIfExists(status);
            throw;
        }

        await auditLogWriter.WriteAsync(
            "outreach_invited",
            "OutreachConversation",
            conversation.Id.ToString(),
            JsonSerializer.Serialize(new { conversationId = conversation.Id }, OutreachMapping.JsonOptions),
            cancellationToken).ConfigureAwait(false);

        return new(OutreachMapping.ToDetail(conversation, [message], OutreachSenderRoles.Organization), Failure.None);
    }

    public static Task<ConversationListResponse> ListForOrganizationAsync(
        Guid organizationId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken) =>
        ListAsync(
            dbContext.OutreachConversations.Where(c => c.OrganizationAccountId == organizationId),
            OutreachSenderRoles.Organization,
            cancellationToken);

    public static Task<Result<ConversationDetailResponse>> GetForOrganizationAsync(
        Guid id,
        Guid organizationId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken) =>
        GetAsync(
            dbContext.OutreachConversations.Where(c => c.Id == id && c.OrganizationAccountId == organizationId),
            OutreachSenderRoles.Organization,
            cancellationToken);

    public static async Task<Result<ConversationDetailResponse>> SendAsOrganizationAsync(
        Guid id,
        string body,
        Guid organizationId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var conversation = await dbContext.OutreachConversations
            .FirstOrDefaultAsync(c => c.Id == id && c.OrganizationAccountId == organizationId, cancellationToken)
            .ConfigureAwait(false);
        if (conversation is null)
        {
            return new(null, Failure.NotFound);
        }

        if (conversation.Status == OutreachStatuses.Declined)
        {
            throw new OutreachDeclinedException();
        }

        if (conversation.Status == OutreachStatuses.Invited)
        {
            throw new OutreachAwaitingReplyException();
        }

        await AddMessageAsync(
            conversation, OutreachSenderRoles.Organization, body, dbContext, auditLogWriter, timeProvider, cancellationToken)
            .ConfigureAwait(false);
        return await GetAsync(
            dbContext.OutreachConversations.Where(c => c.Id == id), OutreachSenderRoles.Organization, cancellationToken)
            .ConfigureAwait(false);
    }

    // ----------------------------------------------------------------- student

    public static Task<ConversationListResponse> ListForStudentAsync(
        Guid studentId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken) =>
        ListAsync(
            dbContext.OutreachConversations.Where(c => c.StudentAccountId == studentId),
            OutreachSenderRoles.Student,
            cancellationToken);

    public static Task<Result<ConversationDetailResponse>> GetForStudentAsync(
        Guid id,
        Guid studentId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken) =>
        GetAsync(
            dbContext.OutreachConversations.Where(c => c.Id == id && c.StudentAccountId == studentId),
            OutreachSenderRoles.Student,
            cancellationToken);

    public static async Task<Result<ConversationDetailResponse>> ReplyAsync(
        Guid id,
        string body,
        Guid studentId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var conversation = await dbContext.OutreachConversations
            .FirstOrDefaultAsync(c => c.Id == id && c.StudentAccountId == studentId, cancellationToken)
            .ConfigureAwait(false);
        if (conversation is null)
        {
            return new(null, Failure.NotFound);
        }

        if (conversation.Status == OutreachStatuses.Declined)
        {
            throw new OutreachDeclinedException();
        }

        if (conversation.Status == OutreachStatuses.Invited)
        {
            conversation.Status = OutreachStatuses.Replied;
        }

        await AddMessageAsync(
            conversation, OutreachSenderRoles.Student, body, dbContext, auditLogWriter, timeProvider, cancellationToken)
            .ConfigureAwait(false);
        return await GetAsync(
            dbContext.OutreachConversations.Where(c => c.Id == id), OutreachSenderRoles.Student, cancellationToken)
            .ConfigureAwait(false);
    }

    public static async Task<Result<ConversationDetailResponse>> DeclineAsync(
        Guid id,
        Guid studentId,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var conversation = await dbContext.OutreachConversations
            .FirstOrDefaultAsync(c => c.Id == id && c.StudentAccountId == studentId, cancellationToken)
            .ConfigureAwait(false);
        if (conversation is null)
        {
            return new(null, Failure.NotFound);
        }

        if (conversation.Status != OutreachStatuses.Declined)
        {
            conversation.Status = OutreachStatuses.Declined;
            conversation.UpdatedAt = timeProvider.GetUtcNow();
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await auditLogWriter.WriteAsync(
                "outreach_declined",
                "OutreachConversation",
                conversation.Id.ToString(),
                JsonSerializer.Serialize(new { conversationId = conversation.Id }, OutreachMapping.JsonOptions),
                cancellationToken).ConfigureAwait(false);
        }

        return await GetAsync(
            dbContext.OutreachConversations.Where(c => c.Id == id), OutreachSenderRoles.Student, cancellationToken)
            .ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ shared

    private static void ThrowIfExists(string? status)
    {
        if (status is null)
        {
            return;
        }

        throw status == OutreachStatuses.Declined
            ? new OutreachDeclinedException()
            : new OutreachAlreadyStartedException();
    }

    private static OutreachMessage NewMessage(Guid conversationId, string senderRole, string body, DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            ConversationId = conversationId,
            SenderRole = senderRole,
            Body = body.Trim(),
            CreatedAt = now,
        };

    private static async Task AddMessageAsync(
        OutreachConversation conversation,
        string senderRole,
        string body,
        WriteDbContext dbContext,
        IAuditLogWriter auditLogWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        conversation.UpdatedAt = now;
        dbContext.OutreachMessages.Add(NewMessage(conversation.Id, senderRole, body, now));
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await auditLogWriter.WriteAsync(
            "outreach_message_sent",
            "OutreachConversation",
            conversation.Id.ToString(),
            JsonSerializer.Serialize(
                new { conversationId = conversation.Id, senderRole }, OutreachMapping.JsonOptions),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ConversationListResponse> ListAsync(
        IQueryable<OutreachConversation> scoped,
        string viewerRole,
        CancellationToken cancellationToken)
    {
        var rows = await scoped
            .AsNoTracking()
            .OrderByDescending(c => c.UpdatedAt)
            .Select(c => new
            {
                Conversation = c,
                Last = c.Messages
                    .OrderByDescending(m => m.CreatedAt)
                    .Select(m => new { m.Body, m.CreatedAt })
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ConversationListResponse(rows
            .Select(r => OutreachMapping.ToSummary(
                r.Conversation, viewerRole, r.Last?.Body ?? string.Empty, r.Last?.CreatedAt ?? r.Conversation.UpdatedAt))
            .ToList());
    }

    private static async Task<Result<ConversationDetailResponse>> GetAsync(
        IQueryable<OutreachConversation> scoped,
        string viewerRole,
        CancellationToken cancellationToken)
    {
        var conversation = await scoped
            .AsNoTracking()
            .Include(c => c.Messages)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return conversation is null
            ? new(null, Failure.NotFound)
            : new(OutreachMapping.ToDetail(conversation, conversation.Messages, viewerRole), Failure.None);
    }
}
