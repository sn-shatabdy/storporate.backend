using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.StudentGrowthExperience.Exceptions;
using Storporate.Modules.StudentGrowthExperience.Requests;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.StudentGrowthExperience.Handlers;

/// <summary>Appends a new student <see cref="ExplorationMessage"/> to an
/// existing exploration and enqueues an
/// <see cref="GrowthJobTypes.AdvisorTurn"/> reply job. Rejects with
/// <see cref="ExplorationBusyException"/> when the exploration is currently
/// in <see cref="ExplorationStatuses.Working"/> so a second concurrent
/// turn never races an in-flight one.</summary>
/// <remarks>
/// <para>
/// <b>Content assembly.</b> The persisted message concatenates the free-text
/// <see cref="AddExplorationMessageRequest.Content"/> with the structured
/// <see cref="AddExplorationMessageRequest.Answers"/> pairs, separated by
/// real newlines (<c>\n\n</c>) so the LLM sees one continuous plain-text
/// block. The UI gets to render the structured pairs separately from the
/// message column; the LLM only reads the persisted column.
/// </para>
/// <para>
/// <b>Atomic write.</b> The status flip to Working, the new message row,
/// and the new <see cref="Job"/> row commit in a single
/// <c>SaveChangesAsync</c> — matching the create-portfolio-item pattern.
/// </para>
/// <para>
/// <b>Tenant isolation.</b> The exploration load runs under the EF global
/// query filter; a cross-account id never matches and the endpoint returns
/// a 404 (the handler returns <see langword="false"/> so the endpoint maps
/// to 404 — same pattern as the portfolio delete handler).
/// </para>
/// </remarks>
public static class AddExplorationMessageHandler
{
    /// <summary>The outcome of <see cref="ExecuteAsync"/>. <c>Accepted = false</c>
/// maps to 404; <c>Accepted = true</c> maps to 202.</summary>
    public sealed record AddOutcome(bool Accepted, Guid ExplorationId, Guid NewJobId);

    public static async Task<AddOutcome> ExecuteAsync(
        Guid explorationId,
        AddExplorationMessageRequest request,
        WriteDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var exploration = await dbContext.Explorations
            .FirstOrDefaultAsync(e => e.Id == explorationId, cancellationToken)
            .ConfigureAwait(false);

        if (exploration is null)
        {
            return new AddOutcome(Accepted: false, ExplorationId: explorationId, NewJobId: Guid.Empty);
        }

        if (exploration.Status == ExplorationStatuses.Working)
        {
            throw new ExplorationBusyException(exploration.Id);
        }

        var content = ComposeContent(request);

        var now = timeProvider.GetUtcNow();
        exploration.Status = ExplorationStatuses.Working;
        exploration.LastError = null;
        exploration.UpdatedAt = now;

        dbContext.ExplorationMessages.Add(new ExplorationMessage
        {
            Id = Guid.NewGuid(),
            AccountId = exploration.AccountId,
            ExplorationId = exploration.Id,
            Role = ExplorationRoles.Student,
            Content = content,
            QuestionsJson = null,
            CreatedAt = now,
        });

        var job = new Job
        {
            Id = Guid.NewGuid(),
            Type = GrowthJobTypes.AdvisorTurn,
            Status = JobStatus.Pending,
            AttemptCount = 0,
            PayloadJson = JsonSerializer.Serialize(
                new AdvisorTurnPayload(exploration.Id, AdvisorTurnModes.Reply)),
            AccountId = exploration.AccountId,
            CreatedAt = now.UtcDateTime,
            UpdatedAt = now.UtcDateTime,
        };
        dbContext.Jobs.Add(job);

        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new AddOutcome(Accepted: true, ExplorationId: exploration.Id, NewJobId: job.Id);
    }

    private static string ComposeContent(AddExplorationMessageRequest request)
    {
        var sb = new StringBuilder();
        var trimmedContent = request.Content?.Trim();
        var hasContent = !string.IsNullOrWhiteSpace(trimmedContent);
        var hasAnswers = request.Answers is { Count: > 0 };

        if (hasContent)
        {
            sb.Append(trimmedContent);
        }
        if (hasAnswers)
        {
            if (hasContent)
            {
                sb.Append("\n\n");
            }
            for (var i = 0; i < request.Answers!.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append("\n\n");
                }
                sb.Append("Question: ").Append(request.Answers[i].Question?.Trim() ?? string.Empty);
                sb.Append('\n');
                sb.Append("Answer: ").Append(request.Answers[i].Answer?.Trim() ?? string.Empty);
            }
        }
        return sb.ToString();
    }
}
