using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Jobs;
using Storporate.Infrastructure.Llm;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.StudentGrowthExperience.Advisor;

/// <summary>
/// Processes a single <see cref="Job"/> of type
/// <see cref="GrowthJobTypes.AdvisorTurn"/> end-to-end: claim the job,
/// load the owning exploration + portfolio + feed under the account scope,
/// build the prompt, call the LLM, parse the JSON, and write one
/// <see cref="ExplorationMessage"/> plus optional
/// <see cref="StudentContextNote"/> / <see cref="ExplorationSummaryVersion"/>
/// rows. Handles three failure paths: malformed payload (terminal failure,
/// no retry), LLM provider failure (retry up to 3 attempts then Failed),
/// and advisor response contract violation (same retry/fail path).
/// </summary>
/// <remarks>
/// <para>
/// <b>Two-scope bracket.</b> Claim runs under
/// <see cref="IBackgroundAccountScope.BeginSystemScope"/> so the EF global
/// query filter's <c>IsAdministrator</c> short-circuit makes every
/// <c>Pending</c> job visible. Once the row is claimed the system scope is
/// disposed and a fresh <c>BeginAccountScope(job.AccountId)</c> is opened
/// for the post-claim work — the same shape the portfolio processor uses.
/// </para>
/// <para>
/// <b>Source-as-snapshot.</b> When the AI returns a suggestion with a
/// <c>sourceItemId</c> that passes the parser's candidate-set check, the
/// stored <see cref="ExplorationSummaryVersion.SuggestionsJson"/> carries the
/// feed item's title / url / sourceName resolved from the database at write
/// time. The AI-supplied title / url is never trusted; the server copies
/// from the row.
/// </para>
/// <para>
/// <b>Atomic write.</b> The advisor message, the context notes, and the
/// summary version (if any) are added to the change tracker together and
/// committed in a single <c>SaveChangesAsync</c>. A half-committed state
/// (message without its summary version, or vice versa) is impossible.
/// </para>
/// <para>
/// <b>No cross-exploration leakage.</b> Each read of
/// <see cref="StudentContextNote"/> is scoped to <c>AccountId == job.AccountId</c>
/// (via the global query filter). Portfolio items are scoped the same way.
/// The prompt builder then sees only the owning account's data.
/// </para>
/// </remarks>
public sealed class AdvisorTurnJobProcessor : IBackgroundJobProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly WriteDbContext _dbContext;
    private readonly ILlmClient _llmClient;
    private readonly AdvisorOptions _advisorOptions;
    private readonly ILogger<AdvisorTurnJobProcessor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IBackgroundAccountScope _accountScope;

    public AdvisorTurnJobProcessor(
        WriteDbContext dbContext,
        ILlmClient llmClient,
        IOptions<AdvisorOptions> advisorOptions,
        ILogger<AdvisorTurnJobProcessor> logger,
        TimeProvider timeProvider,
        IBackgroundAccountScope accountScope)
    {
        _dbContext = dbContext;
        _llmClient = llmClient;
        _advisorOptions = advisorOptions.Value;
        _logger = logger;
        _timeProvider = timeProvider;
        _accountScope = accountScope;
    }

    /// <inheritdoc />
    public string JobType => GrowthJobTypes.AdvisorTurn;

    /// <inheritdoc />
    /// <remarks>
    /// The reaper has already flipped this job to <see cref="JobStatus.Failed"/>
    /// and opened the job's account scope. We mirror the failed state onto the
    /// parent <see cref="Exploration"/> so the UI surfaces the
    /// <see cref="AdvisorDefaults.FriendlyAdvisorError"/> message and the
    /// Retry button becomes available. No-op when the exploration is already
    /// gone (deleted between claim and reaper sweep).
    /// </remarks>
    public async Task OnJobAbandonedAsync(Job job, CancellationToken cancellationToken)
    {
        if (!TryDeserializePayload(job.PayloadJson, out var explorationId))
        {
            return;
        }

        var exploration = await _dbContext.Explorations
            .FirstOrDefaultAsync(e => e.Id == explorationId, cancellationToken)
            .ConfigureAwait(false);
        if (exploration is null)
        {
            return;
        }

        exploration.Status = ExplorationStatuses.Failed;
        exploration.LastError = AdvisorDefaults.FriendlyAdvisorError;
        exploration.UpdatedAt = _timeProvider.GetUtcNow();
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "Reaper abandoned job {JobId} for exploration {ExplorationId}; mirrored Status=Failed.",
            job.Id, explorationId);
    }

    /// <inheritdoc />
    public async Task<BackgroundJobTickOutcome> TryProcessOneAsync(CancellationToken cancellationToken)
    {
        Job? job;
        BackgroundJobTickOutcome claimOutcome;
        DateTime nowUtc;

        using (_accountScope.BeginSystemScope())
        {
            nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            var claim = await JobClaimer.ClaimOneAsync(
                _dbContext,
                GrowthJobTypes.AdvisorTurn,
                nowUtc,
                cancellationToken).ConfigureAwait(false);
            claimOutcome = claim.Outcome;
            job = claim.Job;
        }

        if (claimOutcome != BackgroundJobTickOutcome.Processed || job is null)
        {
            return claimOutcome;
        }

        using (_accountScope.BeginAccountScope(job.AccountId))
        {
            try
            {
                await ProcessClaimedJobAsync(job, nowUtc, cancellationToken).ConfigureAwait(false);
                return BackgroundJobTickOutcome.Processed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Worker shutdown: leave the job in Running so an operator can
                // inspect it. The reaper sweeps it later.
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error processing job {JobId}; treating as retryable.", job.Id);
                var outcome = await JobBookkeeper.RequeueOrFailAsync(
                    _dbContext, job, nowUtc, "Internal processor error: " + ex.Message,
                    _logger, cancellationToken).ConfigureAwait(false);
                if (outcome == JobBookkeeper.RequeueOrFailOutcome.Failed)
                {
                    // Mirror the terminal state onto the exploration so the
                    // UI's status badge flips to Failed in lockstep with the
                    // job's status — the retry-exhausted bookkeeper write
                    // already went out.
                    using (_accountScope.BeginAccountScope(job.AccountId))
                    {
                        var exploration = await _dbContext.Explorations
                            .FirstOrDefaultAsync(
                                e => e.Id == Guid.Parse(
                                    System.Text.Json.JsonSerializer.Deserialize<AdvisorTurnPayload>(
                                        job.PayloadJson)!.ExplorationId.ToString()),
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (exploration is not null)
                        {
                            exploration.Status = ExplorationStatuses.Failed;
                            exploration.LastError = AdvisorDefaults.FriendlyAdvisorError;
                            exploration.UpdatedAt = _timeProvider.GetUtcNow();
                            await _dbContext.SaveChangesAsync(cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                }
                return BackgroundJobTickOutcome.Processed;
            }
        }
    }

    private async Task ProcessClaimedJobAsync(Job job, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        if (!TryDeserializePayload(job.PayloadJson, out var explorationId))
        {
            await JobBookkeeper.MarkFailedAsync(
                _dbContext, job, nowUtc,
                "Job payload was not a valid AdvisorTurn payload.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var exploration = await _dbContext.Explorations
            .FirstOrDefaultAsync(e => e.Id == explorationId, cancellationToken)
            .ConfigureAwait(false);

        if (exploration is null)
        {
            await JobBookkeeper.MarkFailedAsync(
                _dbContext, job, nowUtc,
                $"Referenced exploration '{explorationId}' was not found.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var payload = JsonSerializer.Deserialize<AdvisorTurnPayload>(job.PayloadJson, JsonOptions);
        var mode = payload?.Mode ?? AdvisorTurnModes.Opening;

        // Optimistically flip to Working so the UI's status badge updates
        // between the handler returning and the (potentially slow) LLM call.
        // Cleared back to Idle at the bottom on the success path; left as
        // Failed on the retry-exhausted path (bookkeeper sets the job status,
        // and the reaper hook handles that path).
        exploration.Status = ExplorationStatuses.Working;
        exploration.LastError = null;
        exploration.UpdatedAt = _timeProvider.GetUtcNow();

        // Load all inputs the prompt builder needs. Every query runs under the
        // global query filter so the results are scoped to job.AccountId.
        var contextNotes = await _dbContext.StudentContextNotes
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => n.Text)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        AdvisorParsedSummary? currentSummary = null;
        if (mode != AdvisorTurnModes.Opening)
        {
            currentSummary = await LoadCurrentSummaryAsync(exploration.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        var history = await LoadHistoryAsync(exploration.Id, cancellationToken)
            .ConfigureAwait(false);

        var feedCandidates = await _dbContext.FeedItems
            .OrderByDescending(f => f.FetchedAt)
            .Take(_advisorOptions.MaxFeedCandidates)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var portfolioItems = await LoadPortfolioItemsAsync(exploration.AccountId, cancellationToken)
            .ConfigureAwait(false);

        var promptInputs = new AdvisorPromptInputs(
            Mode: mode,
            ContextNotes: contextNotes,
            CurrentSummary: currentSummary,
            FeedCandidates: feedCandidates,
            PortfolioItems: portfolioItems,
            History: history,
            UserPrompt: BuildUserPromptForMode(mode, exploration, history));

        var completionRequest = AdvisorPromptBuilder.BuildCompletionRequest(promptInputs, _advisorOptions);

        LlmCompletionResult completion;
        try
        {
            completion = await _llmClient.CompleteAsync(completionRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LlmProviderException ex)
        {
            var outcome = await JobBookkeeper.RequeueOrFailAsync(
                _dbContext, job, nowUtc, "LLM provider error: " + ex.Message,
                _logger, cancellationToken).ConfigureAwait(false);
            await MarkExplorationTerminalIfNeededAsync(
                exploration, outcome, "The advisor could not finish this. Try again.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        AdvisorParsedResponse parsed;
        try
        {
            parsed = AdvisorResponseParser.Parse(
                completion.OutputText,
                candidateIds: feedCandidates.Select(f => f.Id).ToHashSet(),
                logger: _logger);
        }
        catch (AdvisorResponseInvalidException ex)
        {
            var outcome = await JobBookkeeper.RequeueOrFailAsync(
                _dbContext, job, nowUtc, "Advisor response invalid: " + ex.Message,
                _logger, cancellationToken).ConfigureAwait(false);
            await MarkExplorationTerminalIfNeededAsync(
                exploration, outcome, "The advisor could not finish this. Try again.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // Source-as-snapshot: resolve each AI-cited source id to the live
        // feed-item row at write time. The stored SuggestionsJson carries the
        // row's title / url / sourceName verbatim — the AI's text is not
        // trusted for those fields.
        var suggestionSnapshots = ResolveSuggestionSnapshots(parsed.Summary?.Suggestions, feedCandidates);

        // Write advisor message (Content = reply, or short neutral line if only
        // questions came back; QuestionsJson when questions present).
        var messageContent = !string.IsNullOrWhiteSpace(parsed.Reply)
            ? parsed.Reply!
            : "I'd like to ask a couple of questions before going further.";
        var questionsJson = parsed.Questions.Count > 0
            ? JsonSerializer.Serialize(parsed.Questions, JsonOptions)
            : null;

        var now = _timeProvider.GetUtcNow();
        _dbContext.ExplorationMessages.Add(new ExplorationMessage
        {
            Id = Guid.NewGuid(),
            AccountId = exploration.AccountId,
            ExplorationId = exploration.Id,
            Role = ExplorationRoles.Advisor,
            Content = messageContent,
            QuestionsJson = questionsJson,
            CreatedAt = now,
        });

        // Write context notes (skip case-insensitive duplicates of existing texts).
        var existingNoteTexts = new HashSet<string>(
            contextNotes,
            StringComparer.OrdinalIgnoreCase);
        if (parsed.ContextNotes.Count > 0)
        {
            foreach (var note in parsed.ContextNotes)
            {
                if (!existingNoteTexts.Add(note))
                {
                    continue;
                }
                _dbContext.StudentContextNotes.Add(new StudentContextNote
                {
                    Id = Guid.NewGuid(),
                    AccountId = exploration.AccountId,
                    ExplorationId = exploration.Id,
                    Text = note,
                    CreatedAt = now,
                });
            }
        }

        // Write summary version (only when AI supplied one; VersionNumber = max+1).
        int? versionNumber = null;
        if (parsed.Summary is not null
            && (parsed.Summary.Gaps.Count > 0 || parsed.Summary.Suggestions.Count > 0))
        {
            var previousVersion = await _dbContext.ExplorationSummaryVersions
                .Where(s => s.ExplorationId == exploration.Id)
                .OrderByDescending(s => s.VersionNumber)
                .Select(s => (int?)s.VersionNumber)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            var nextVersion = (previousVersion ?? 0) + 1;
            versionNumber = nextVersion;

            var changeNote = parsed.Summary.ChangeNote
                ?? (nextVersion > 1 ? "Summary updated." : null);

            var gapsJson = JsonSerializer.Serialize(parsed.Summary.Gaps, JsonOptions);
            var suggestionsJson = JsonSerializer.Serialize(suggestionSnapshots, JsonOptions);

            _dbContext.ExplorationSummaryVersions.Add(new ExplorationSummaryVersion
            {
                Id = Guid.NewGuid(),
                AccountId = exploration.AccountId,
                ExplorationId = exploration.Id,
                VersionNumber = nextVersion,
                GapsJson = gapsJson,
                SuggestionsJson = suggestionsJson,
                ChangeNote = changeNote,
                CreatedAt = now,
            });
        }

        // Set title only when still equal to the default placeholder, so the
        // student's later PUT /title write is never silently overwritten.
        if (!string.IsNullOrWhiteSpace(parsed.Title)
            && exploration.Title == AdvisorDefaults.DefaultExplorationTitle)
        {
            exploration.Title = parsed.Title!;
        }

        exploration.Status = ExplorationStatuses.Idle;
        exploration.LastError = null;
        exploration.UpdatedAt = now;

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await JobBookkeeper.MarkSucceededAsync(
            _dbContext, job, nowUtc, cancellationToken).ConfigureAwait(false);

        sw.Stop();
        _logger.LogInformation(
            "AdvisorTurnSucceeded: JobId={JobId} ExplorationId={ExplorationId} AccountId={AccountId} DurationMs={DurationMs} PromptTokens={PromptTokens} CompletionTokens={CompletionTokens} TotalTokens={TotalTokens} QuestionCount={QuestionCount} GapCount={GapCount} SuggestionCount={SuggestionCount} NoteCount={NoteCount} VersionNumber={VersionNumber} Mode={Mode}",
            job.Id,
            exploration.Id,
            exploration.AccountId,
            (long)sw.Elapsed.TotalMilliseconds,
            completion.Usage?.PromptTokens,
            completion.Usage?.CompletionTokens,
            completion.Usage?.TotalTokens,
            parsed.Questions.Count,
            parsed.Summary?.Gaps.Count ?? 0,
            suggestionSnapshots.Count,
            parsed.ContextNotes.Count,
            versionNumber,
            mode);
    }

    private async Task<AdvisorParsedSummary?> LoadCurrentSummaryAsync(
        Guid explorationId, CancellationToken cancellationToken)
    {
        var latest = await _dbContext.ExplorationSummaryVersions
            .Where(s => s.ExplorationId == explorationId)
            .OrderByDescending(s => s.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (latest is null)
        {
            return null;
        }

        AdvisorParsedGap[] gaps;
        try
        {
            gaps = JsonSerializer.Deserialize<AdvisorParsedGap[]>(latest.GapsJson, JsonOptions) ?? Array.Empty<AdvisorParsedGap>();
        }
        catch (JsonException)
        {
            gaps = Array.Empty<AdvisorParsedGap>();
        }

        AdvisorParsedSuggestion[] suggestions;
        try
        {
            // The stored SuggestionsJson may carry the source-snapshot shape
            // (with `Source` populated). Read into a more permissive DTO so we
            // can repopulate the parser shape from either form.
            var stored = JsonSerializer.Deserialize<List<StoredSuggestionDto>>(
                latest.SuggestionsJson, JsonOptions) ?? new List<StoredSuggestionDto>();
            suggestions = stored
                .Select(s => new AdvisorParsedSuggestion(s.Title, s.Reason, s.NextStep, s.Source?.FeedItemId))
                .ToArray();
        }
        catch (JsonException)
        {
            suggestions = Array.Empty<AdvisorParsedSuggestion>();
        }

        return new AdvisorParsedSummary(gaps, suggestions, latest.ChangeNote);
    }

    private async Task<IReadOnlyList<LlmChatMessage>> LoadHistoryAsync(
        Guid explorationId, CancellationToken cancellationToken)
    {
        var windowSize = _advisorOptions.HistoryWindowTurns * 2;
        var messages = await _dbContext.ExplorationMessages
            .Where(m => m.ExplorationId == explorationId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(windowSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        messages.Reverse(); // oldest first in the prompt

        return messages.Select(m => new LlmChatMessage(
            Role: m.Role == ExplorationRoles.Student ? "user" : "assistant",
            Content: m.Content)).ToList();
    }

    private async Task<IReadOnlyList<PortfolioItemSnapshot>> LoadPortfolioItemsAsync(
        Guid accountId, CancellationToken cancellationToken)
    {
        // Newest first; the prompt builder appends until the budget is exhausted.
        var items = await _dbContext.PortfolioItems
            .Where(p => p.AccountId == accountId)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (items.Count == 0)
        {
            return Array.Empty<PortfolioItemSnapshot>();
        }

        var itemIds = items.Select(i => i.Id).ToHashSet();
        var findings = await _dbContext.PortfolioSkillFindings
            .Where(f => itemIds.Contains(f.PortfolioItemId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var findingsByItem = findings
            .GroupBy(f => f.PortfolioItemId)
            .ToDictionary(g => g.Key, g => g.ToList());

        return items.Select(p => new PortfolioItemSnapshot(
            Label: p.Label,
            Description: p.Description,
            Findings: findingsByItem.TryGetValue(p.Id, out var list)
                ? list
                    .Select(f => new FindingSnapshot(f.SkillName, f.ConfidenceBand, f.Explanation))
                    .ToList()
                : new List<FindingSnapshot>()
        )).ToList();
    }

    private static IReadOnlyList<AdvisorSuggestionSnapshot> ResolveSuggestionSnapshots(
        IReadOnlyList<AdvisorParsedSuggestion>? suggestions,
        IReadOnlyList<FeedItem> feedCandidates)
    {
        if (suggestions is null || suggestions.Count == 0)
        {
            return Array.Empty<AdvisorSuggestionSnapshot>();
        }

        var byId = feedCandidates.ToDictionary(f => f.Id);
        var snapshots = new List<AdvisorSuggestionSnapshot>(suggestions.Count);
        foreach (var suggestion in suggestions)
        {
            AdvisorSuggestionSourceSnapshot? source = null;
            if (suggestion.SourceFeedItemId is Guid id && byId.TryGetValue(id, out var feed))
            {
                source = new AdvisorSuggestionSourceSnapshot(
                    FeedItemId: feed.Id,
                    Title: feed.Title,
                    Url: feed.Url,
                    SourceName: feed.SourceName);
            }
            snapshots.Add(new AdvisorSuggestionSnapshot(
                Title: suggestion.Title,
                Reason: suggestion.Reason,
                NextStep: suggestion.NextStep,
                Source: source));
        }
        return snapshots;
    }

    private static string BuildUserPromptForMode(
        string mode,
        Exploration exploration,
        IReadOnlyList<LlmChatMessage> history)
    {
        // The Opening / Refresh modes carry no fresh user message; the
        // prompt's STUDENT MESSAGE section gets a short framing line so the
        // model knows what to do. Reply mode expects the student message to
        // already be in the history (the handler added it before enqueue);
        // re-using the very last history entry here would duplicate it, so
        // Reply returns empty and the prompt builder emits an empty
        // STUDENT MESSAGE section (acceptable per the prompt contract).
        return mode switch
        {
            AdvisorTurnModes.Opening => exploration.Title == AdvisorDefaults.DefaultExplorationTitle
                ? "Start a new exploration."
                : $"Continue the exploration titled '{exploration.Title}'.",
            AdvisorTurnModes.Refresh => "Refresh the summary based on everything so far.",
            AdvisorTurnModes.Reply => string.Empty,
            _ => string.Empty,
        };
    }

    private static bool TryDeserializePayload(string payloadJson, out Guid explorationId)
    {
        explorationId = Guid.Empty;
        try
        {
            var payload = JsonSerializer.Deserialize<AdvisorTurnPayload>(payloadJson, JsonOptions);
            if (payload is null)
            {
                return false;
            }
            explorationId = payload.ExplorationId;
            return explorationId != Guid.Empty;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // DTOs used when reading previously-stored suggestion snapshots back into
    // memory for the next turn's CURRENT SUMMARY section. The parser shape
    // uses Guid? SourceFeedItemId; the stored shape uses a nested object so
    // the suggestion can render the title / url / sourceName verbatim.
    private sealed class StoredSuggestionDto
    {
        public string Title { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string NextStep { get; set; } = string.Empty;
        public StoredSuggestionSourceDto? Source { get; set; }
    }

    private sealed class StoredSuggestionSourceDto
    {
        public Guid FeedItemId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string SourceName { get; set; } = string.Empty;
    }

    /// <summary>
    /// Mirror a terminal <see cref="JobStatus.Failed"/> onto the parent
    /// <see cref="Exploration"/> so the UI's status badge flips in lockstep
    /// with the job's status. No-op when the bookkeeper only re-queued the
    /// job for another attempt (attempts &lt; <see cref="JobBookkeeper.MaxAttempts"/>).
    /// The write piggy-backs on the same <c>SaveChangesAsync</c> the bookkeeper
    /// already issued — appending the row update here means a single commit,
    /// half-state impossible.
    /// </summary>
    private async Task MarkExplorationTerminalIfNeededAsync(
        Exploration exploration,
        JobBookkeeper.RequeueOrFailOutcome outcome,
        string lastError,
        CancellationToken cancellationToken)
    {
        if (outcome != JobBookkeeper.RequeueOrFailOutcome.Failed)
        {
            return;
        }

        exploration.Status = ExplorationStatuses.Failed;
        exploration.LastError = lastError;
        exploration.UpdatedAt = _timeProvider.GetUtcNow();
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>The on-disk shape of a single advisor suggestion. Mirrors the
/// <see cref="AdvisorParsedSuggestion"/> parser shape but replaces the bare
/// <c>sourceItemId</c> GUID with a full snapshot object so the read endpoint
/// can render the cited feed item's title / url / sourceName without
/// re-querying the database.</summary>
public sealed record AdvisorSuggestionSnapshot(
    string Title,
    string Reason,
    string NextStep,
    AdvisorSuggestionSourceSnapshot? Source);

/// <summary>The source-as-snapshot block on a stored
/// <see cref="AdvisorSuggestionSnapshot"/>. Always copied from the live
/// <see cref="FeedItem"/> row at write time — never populated from the
/// AI's text.</summary>
public sealed record AdvisorSuggestionSourceSnapshot(
    Guid FeedItemId,
    string Title,
    string Url,
    string SourceName);
