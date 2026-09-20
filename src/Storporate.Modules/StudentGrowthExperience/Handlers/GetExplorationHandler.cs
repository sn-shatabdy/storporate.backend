using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.StudentGrowthExperience.Advisor;
using Storporate.Modules.StudentGrowthExperience.Responses;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.StudentGrowthExperience.Handlers;

/// <summary>Returns the full detail shape for a single exploration —
/// metadata + messages (chronological) + the latest summary version (when
/// one exists). Returns <see langword="null"/> when the id doesn't match
/// the caller's account, so the endpoint maps to 404.</summary>
public static class GetExplorationHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<ExplorationDetailResponse?> ExecuteAsync(
        Guid explorationId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dbContext);

        var exploration = await dbContext.Explorations
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == explorationId, cancellationToken)
            .ConfigureAwait(false);
        if (exploration is null)
        {
            return null;
        }

        var messages = await dbContext.ExplorationMessages
            .AsNoTracking()
            .Where(m => m.ExplorationId == explorationId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var latestSummary = await dbContext.ExplorationSummaryVersions
            .AsNoTracking()
            .Where(s => s.ExplorationId == explorationId)
            .OrderByDescending(s => s.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new ExplorationDetailResponse(
            Id: exploration.Id,
            Title: exploration.Title,
            Status: exploration.Status,
            LastError: exploration.LastError,
            CreatedAt: exploration.CreatedAt,
            UpdatedAt: exploration.UpdatedAt,
            Messages: messages.Select(MapMessage).ToList(),
            LatestSummary: latestSummary is null ? null : MapSummary(latestSummary));
    }

    private static ExplorationMessageResponse MapMessage(ExplorationMessage m)
    {
        IReadOnlyList<ExplorationQuestionResponse>? questions = null;
        if (!string.IsNullOrWhiteSpace(m.QuestionsJson))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<AdvisorParsedQuestion>>(
                    m.QuestionsJson, JsonOptions);
                if (parsed is { Count: > 0 })
                {
                    questions = parsed
                        .Select(q => new ExplorationQuestionResponse(
                            Prompt: q.Prompt,
                            Options: q.Options.ToList()))
                        .ToList();
                }
            }
            catch (JsonException)
            {
                // Bad JSON in storage: treat as no questions rather than failing the read.
                questions = null;
            }
        }
        return new ExplorationMessageResponse(
            Id: m.Id,
            Role: m.Role,
            Content: m.Content,
            Questions: questions,
            CreatedAt: m.CreatedAt);
    }

    private static ExplorationSummaryResponse MapSummary(ExplorationSummaryVersion s)
    {
        List<AdvisorParsedGap> gaps = new();
        if (!string.IsNullOrWhiteSpace(s.GapsJson))
        {
            try
            {
                gaps = JsonSerializer.Deserialize<List<AdvisorParsedGap>>(s.GapsJson, JsonOptions)
                    ?? new List<AdvisorParsedGap>();
            }
            catch (JsonException)
            {
                gaps = new List<AdvisorParsedGap>();
            }
        }
        List<AdvisorSuggestionSnapshot> suggestions = new();
        if (!string.IsNullOrWhiteSpace(s.SuggestionsJson))
        {
            try
            {
                suggestions = JsonSerializer.Deserialize<List<AdvisorSuggestionSnapshot>>(
                    s.SuggestionsJson, JsonOptions) ?? new List<AdvisorSuggestionSnapshot>();
            }
            catch (JsonException)
            {
                suggestions = new List<AdvisorSuggestionSnapshot>();
            }
        }
        return new ExplorationSummaryResponse(
            VersionNumber: s.VersionNumber,
            CreatedAt: s.CreatedAt,
            ChangeNote: s.ChangeNote,
            Gaps: gaps.Select(g => new ExplorationGapResponse(
                Title: g.Title, Detail: g.Detail, Band: g.Band)).ToList(),
            Suggestions: suggestions.Select(MapSuggestion).ToList());
    }

    private static ExplorationSuggestionResponse MapSuggestion(AdvisorSuggestionSnapshot snap) =>
        new(
            Title: snap.Title,
            Reason: snap.Reason,
            NextStep: snap.NextStep,
            Source: snap.Source is null
                ? null
                : new ExplorationSuggestionSourceResponse(
                    FeedItemId: snap.Source.FeedItemId,
                    Title: snap.Source.Title,
                    Url: snap.Source.Url,
                    SourceName: snap.Source.SourceName));
}
