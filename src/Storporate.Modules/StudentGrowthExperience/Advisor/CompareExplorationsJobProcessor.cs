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
/// <see cref="GrowthJobTypes.CompareExplorations"/> end-to-end: claim the
/// job, load the owning comparison + both explorations + their latest
/// summary versions under the account scope, build a comparison prompt,
/// call the LLM, parse a single <c>reply</c> field, and write the result
/// text back on the comparison row.
/// </summary>
/// <remarks>
/// <para>
/// <b>Same two-scope bracket as <see cref="AdvisorTurnJobProcessor"/>.</b>
/// Claim runs under
/// <see cref="IBackgroundAccountScope.BeginSystemScope"/>; the rest runs
/// under <c>BeginAccountScope(job.AccountId)</c>.
/// </para>
/// <para>
/// <b>Comparison JSON contract.</b> The prompt asks for a single object with
/// a <c>reply</c> field (no questions, no suggestions). The parser keeps
/// only the reply, capped at 6 000 characters — matching the advisor turn's
/// reply cap.
/// </para>
/// <para>
/// <b>Retry / fail policy.</b> Identical to the advisor turn: LLM provider
/// failure and contract violation both flow through
/// <see cref="JobBookkeeper.RequeueOrFailAsync"/>. After
/// <see cref="JobBookkeeper.MaxAttempts"/> attempts the comparison row is
/// moved to <see cref="ExplorationComparisonStatuses.Failed"/>.
/// </para>
/// </remarks>
public sealed class CompareExplorationsJobProcessor : IBackgroundJobProcessor
{
    private const int MaxReplyCharacters = 6_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly WriteDbContext _dbContext;
    private readonly ILlmClient _llmClient;
    private readonly AdvisorOptions _advisorOptions;
    private readonly ILogger<CompareExplorationsJobProcessor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IBackgroundAccountScope _accountScope;

    public CompareExplorationsJobProcessor(
        WriteDbContext dbContext,
        ILlmClient llmClient,
        IOptions<AdvisorOptions> advisorOptions,
        ILogger<CompareExplorationsJobProcessor> logger,
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
    public string JobType => GrowthJobTypes.CompareExplorations;

    /// <inheritdoc />
    public async Task OnJobAbandonedAsync(Job job, CancellationToken cancellationToken)
    {
        if (!TryDeserializePayload(job.PayloadJson, out var comparisonId))
        {
            return;
        }

        var comparison = await _dbContext.ExplorationComparisons
            .FirstOrDefaultAsync(c => c.Id == comparisonId, cancellationToken)
            .ConfigureAwait(false);
        if (comparison is null)
        {
            return;
        }

        comparison.Status = ExplorationComparisonStatuses.Failed;
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "Reaper abandoned job {JobId} for comparison {ComparisonId}; mirrored Status=Failed.",
            job.Id, comparisonId);
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
                GrowthJobTypes.CompareExplorations,
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
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error processing compare job {JobId}; treating as retryable.", job.Id);
                await JobBookkeeper.RequeueOrFailAsync(
                    _dbContext, job, nowUtc, "Internal processor error: " + ex.Message,
                    _logger, cancellationToken).ConfigureAwait(false);
                return BackgroundJobTickOutcome.Processed;
            }
        }
    }

    private async Task ProcessClaimedJobAsync(Job job, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        if (!TryDeserializePayload(job.PayloadJson, out var comparisonId))
        {
            await JobBookkeeper.MarkFailedAsync(
                _dbContext, job, nowUtc,
                "Job payload was not a valid CompareExplorations payload.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var comparison = await _dbContext.ExplorationComparisons
            .FirstOrDefaultAsync(c => c.Id == comparisonId, cancellationToken)
            .ConfigureAwait(false);
        if (comparison is null)
        {
            await JobBookkeeper.MarkFailedAsync(
                _dbContext, job, nowUtc,
                $"Referenced comparison '{comparisonId}' was not found.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        // Load both explorations and their latest summaries. Each read is
        // scoped to job.AccountId via the global query filter; a cross-account
        // id never matches.
        var first = await LoadExplorationSnapshotAsync(comparison.FirstExplorationId, cancellationToken)
            .ConfigureAwait(false);
        var second = await LoadExplorationSnapshotAsync(comparison.SecondExplorationId, cancellationToken)
            .ConfigureAwait(false);
        if (first is null || second is null)
        {
            await JobBookkeeper.MarkFailedAsync(
                _dbContext, job, nowUtc,
                "Comparison references a missing exploration.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var contextNotes = await _dbContext.StudentContextNotes
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => n.Text)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var userPrompt = BuildUserPrompt(first, second, contextNotes);
        var systemPrompt = BuildSystemPrompt();

        var request = new LlmCompletionRequest(
            UserPrompt: userPrompt,
            SystemPrompt: systemPrompt,
            MaxOutputTokens: _advisorOptions.MaxOutputTokens);

        LlmCompletionResult completion;
        try
        {
            completion = await _llmClient.CompleteAsync(request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LlmProviderException ex)
        {
            var outcome = await JobBookkeeper.RequeueOrFailAsync(
                _dbContext, job, nowUtc, "LLM provider error: " + ex.Message,
                _logger, cancellationToken).ConfigureAwait(false);
            await MarkComparisonTerminalIfNeededAsync(
                comparison, outcome, cancellationToken).ConfigureAwait(false);
            return;
        }

        string reply;
        try
        {
            reply = ParseCompareReply(completion.OutputText);
        }
        catch (AdvisorResponseInvalidException ex)
        {
            var outcome = await JobBookkeeper.RequeueOrFailAsync(
                _dbContext, job, nowUtc, "Compare response invalid: " + ex.Message,
                _logger, cancellationToken).ConfigureAwait(false);
            await MarkComparisonTerminalIfNeededAsync(
                comparison, outcome, cancellationToken).ConfigureAwait(false);
            return;
        }

        comparison.Status = ExplorationComparisonStatuses.Completed;
        comparison.ResultText = reply;
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await JobBookkeeper.MarkSucceededAsync(
            _dbContext, job, nowUtc, cancellationToken).ConfigureAwait(false);

        sw.Stop();
        _logger.LogInformation(
            "CompareExplorationsSucceeded: JobId={JobId} ComparisonId={ComparisonId} AccountId={AccountId} DurationMs={DurationMs} PromptTokens={PromptTokens} CompletionTokens={CompletionTokens} TotalTokens={TotalTokens}",
            job.Id, comparison.Id, comparison.AccountId,
            (long)sw.Elapsed.TotalMilliseconds,
            completion.Usage?.PromptTokens,
            completion.Usage?.CompletionTokens,
            completion.Usage?.TotalTokens);
    }

    private async Task<CompareExplorationSnapshot?> LoadExplorationSnapshotAsync(
        Guid explorationId, CancellationToken cancellationToken)
    {
        var exploration = await _dbContext.Explorations
            .FirstOrDefaultAsync(e => e.Id == explorationId, cancellationToken)
            .ConfigureAwait(false);
        if (exploration is null)
        {
            return null;
        }

        var summary = await _dbContext.ExplorationSummaryVersions
            .Where(s => s.ExplorationId == explorationId)
            .OrderByDescending(s => s.VersionNumber)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new CompareExplorationSnapshot(
            Title: exploration.Title,
            GapsJson: summary?.GapsJson,
            SuggestionsJson: summary?.SuggestionsJson);
    }

    private static string BuildSystemPrompt() =>
        """
        You are comparing two personal-exploration directions a single student
        has been thinking about. Each direction has its own gaps and
        suggestions list. Produce a single JSON object with one field:

          { "reply": string }

        The reply is your plain-text comparison — what each direction needs,
        how they overlap, and which one looks closer to a concrete next step
        given what the student has shown. Plain text only, short paragraphs,
        no markdown. No numeric scores. No invented facts.

        Return STRICT JSON (no prose, no fences).
        """;

    private static string BuildUserPrompt(
        CompareExplorationSnapshot first,
        CompareExplorationSnapshot second,
        IReadOnlyList<string> contextNotes)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("--- STUDENT CONTEXT NOTES (untrusted) ---");
        if (contextNotes.Count == 0)
        {
            sb.AppendLine("(none)");
        }
        else
        {
            foreach (var note in contextNotes)
            {
                sb.AppendLine($"- {note}");
            }
        }
        sb.AppendLine();
        sb.AppendLine($"--- EXPLORATION A: {first.Title} ---");
        sb.AppendLine("Gaps:");
        sb.AppendLine(string.IsNullOrWhiteSpace(first.GapsJson) ? "(none)" : first.GapsJson);
        sb.AppendLine("Suggestions:");
        sb.AppendLine(string.IsNullOrWhiteSpace(first.SuggestionsJson) ? "(none)" : first.SuggestionsJson);
        sb.AppendLine();
        sb.AppendLine($"--- EXPLORATION B: {second.Title} ---");
        sb.AppendLine("Gaps:");
        sb.AppendLine(string.IsNullOrWhiteSpace(second.GapsJson) ? "(none)" : second.GapsJson);
        sb.AppendLine("Suggestions:");
        sb.AppendLine(string.IsNullOrWhiteSpace(second.SuggestionsJson) ? "(none)" : second.SuggestionsJson);
        sb.AppendLine();
        sb.AppendLine("--- TASK ---");
        sb.AppendLine("Compare the two explorations and return the JSON.");
        return sb.ToString();
    }

    private static string ParseCompareReply(string? outputText)
    {
        if (string.IsNullOrWhiteSpace(outputText))
        {
            throw new AdvisorResponseInvalidException("Compare LLM returned an empty response body.");
        }

        AdvisorJsonCompareDto? dto;
        try
        {
            dto = LlmJsonExtractor.Deserialize<AdvisorJsonCompareDto>(
                outputText, AdvisorJsonSerializerOptions.CamelCase);
        }
        catch (JsonException ex)
        {
            throw new AdvisorResponseInvalidException(
                "Compare LLM response was not valid JSON: " + ex.Message, ex);
        }

        if (dto is null || string.IsNullOrWhiteSpace(dto.Reply))
        {
            throw new AdvisorResponseInvalidException(
                "Compare LLM response did not contain a non-empty reply field.");
        }

        var trimmed = dto.Reply.Trim();
        return trimmed.Length <= MaxReplyCharacters
            ? trimmed
            : trimmed[..MaxReplyCharacters];
    }

    private static bool TryDeserializePayload(string payloadJson, out Guid comparisonId)
    {
        comparisonId = Guid.Empty;
        try
        {
            var payload = JsonSerializer.Deserialize<CompareExplorationsPayload>(payloadJson, JsonOptions);
            if (payload is null)
            {
                return false;
            }
            comparisonId = payload.ComparisonId;
            return comparisonId != Guid.Empty;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record CompareExplorationSnapshot(
        string Title,
        string? GapsJson,
        string? SuggestionsJson);

    private sealed class AdvisorJsonCompareDto
    {
        public string? Reply { get; set; }
    }

    /// <summary>
    /// Mirror a terminal <see cref="JobStatus.Failed"/> onto the parent
    /// <see cref="ExplorationComparison"/> so the API's status flips in
    /// lockstep with the job. No-op when the bookkeeper only re-queued
    /// the job (attempts &lt; <see cref="JobBookkeeper.MaxAttempts"/>).
    /// </summary>
    private async Task MarkComparisonTerminalIfNeededAsync(
        ExplorationComparison comparison,
        JobBookkeeper.RequeueOrFailOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (outcome != JobBookkeeper.RequeueOrFailOutcome.Failed)
        {
            return;
        }

        comparison.Status = ExplorationComparisonStatuses.Failed;
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
