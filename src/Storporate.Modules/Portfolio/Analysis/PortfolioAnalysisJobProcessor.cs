using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Jobs;
using Storporate.Infrastructure.Llm;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel;

namespace Storporate.Modules.Portfolio.Analysis;

/// <summary>
/// Processes a single <see cref="Job"/> of type <see cref="PortfolioJobTypes.AnalyzePortfolioItem"/>
/// end-to-end: claim one <c>Pending</c> job, extract analyzable text from the
/// referenced <see cref="PortfolioItem"/> via
/// <see cref="EvidenceContentExtractor"/>, prompt the LLM via <see cref="ILlmClient"/>,
/// parse the JSON skills response, and write one <see cref="PortfolioSkillFinding"/>
/// row per returned skill. Handles three failure paths: unsupported evidence type
/// (short-circuit, no LLM call), LLM provider failure (retry up to 3 attempts then
/// <c>Failed</c>), and malformed JSON from the model (same retry/fail path).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a row-claim instead of EF tracking.</b> The worker is a polling loop — two
/// ticks (or two processes) could race on the same <c>Pending</c> job. The claim
/// runs as a single atomic <c>UPDATE ... WHERE Status = 'Pending'</c> via
/// <see cref="ExecuteUpdateAsync"/> so only one tick ever transitions the row to
/// <c>Running</c>; the rest see no row updated and return
/// <see cref="BackgroundJobTickOutcome.ClaimedByAnother"/>. This is the pattern the
/// Phase 2 acceptance criterion "running two worker ticks back-to-back against the
/// same seeded Pending job produces exactly one Succeeded outcome" pins.
/// </para>
/// <para>
/// <b>Retry policy.</b> On <see cref="LlmProviderException"/> or a JSON parse
/// failure, <see cref="Job.AttemptCount"/> is incremented. Below the cap (3) the
/// status is set back to <c>Pending</c> with a fresh <c>UpdatedAt</c> so a later
/// tick retries it. At the cap, <c>Status = Failed</c>,
/// <c>ErrorMessage</c> populated, and the item's <see cref="PortfolioItem.AnalysisStatus"/>
/// flipped to <c>Failed</c>. A retry-eligible job's <c>Status = Pending</c> makes
/// it eligible for the very next tick — that's the intended behavior, not a bug,
/// so the worker re-finds it on its next poll rather than waiting for the 5-second
/// interval to elapse.
/// </para>
/// <para>
/// <b>No re-extraction on retry.</b> A retried job re-runs extraction and re-prompts
/// the LLM from scratch. The artifact bytes are still in <see cref="IArtifactStore"/>
/// for file submissions; link submissions reconstruct from the item's fields. The
/// extractor is therefore safe to call repeatedly — it's idempotent over its inputs.
/// </para>
/// </remarks>
public sealed class PortfolioAnalysisJobProcessor : IBackgroundJobProcessor
{
    /// <summary>The maximum number of processing attempts before a job is moved
    /// to <see cref="JobStatus.Failed"/>. Each attempt runs the full
    /// extract → LLM → parse pipeline; failures of any step count.</summary>
    public const int MaxAttempts = 3;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly WriteDbContext _dbContext;
    private readonly ILlmClient _llmClient;
    private readonly EvidenceContentExtractor _extractor;
    private readonly ILogger<PortfolioAnalysisJobProcessor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IBackgroundAccountScope _accountScope;

    public PortfolioAnalysisJobProcessor(
        WriteDbContext dbContext,
        ILlmClient llmClient,
        EvidenceContentExtractor extractor,
        ILogger<PortfolioAnalysisJobProcessor> logger,
        TimeProvider timeProvider,
        IBackgroundAccountScope accountScope)
    {
        _dbContext = dbContext;
        _llmClient = llmClient;
        _extractor = extractor;
        _logger = logger;
        _timeProvider = timeProvider;
        _accountScope = accountScope;
    }

    /// <inheritdoc />
    public string JobType => PortfolioJobTypes.AnalyzePortfolioItem;

    /// <inheritdoc />
    /// <remarks>
    /// The <see cref="StaleJobReaper"/> calls this after it has flipped a
    /// <see cref="Job"/> of <see cref="PortfolioJobTypes.AnalyzePortfolioItem"/> to
    /// <see cref="JobStatus.Failed"/> because it was stuck in <see cref="JobStatus.Running"/>
    /// for longer than <see cref="BackgroundJobOptions.StaleRunningAfter"/>. The worker
    /// has already opened the job's account scope for us (so
    /// <see cref="IAccountContext.AccountId"/> matches <c>job.AccountId</c>), we just
    /// need to mirror the failed state onto the parent <see cref="PortfolioItem"/>
    /// so the UI's analysis badge flips to <see cref="PortfolioAnalysisStatuses.Failed"/>
    /// and the Retry button becomes available.
    /// </remarks>
    public async Task OnJobAbandonedAsync(Job job, CancellationToken cancellationToken)
    {
        if (!TryDeserializePayload(job.PayloadJson, out var portfolioItemId))
        {
            return;
        }

        var item = await _dbContext.PortfolioItems
            .FirstOrDefaultAsync(i => i.Id == portfolioItemId, cancellationToken)
            .ConfigureAwait(false);
        if (item is null)
        {
            return;
        }

        item.AnalysisStatus = PortfolioAnalysisStatuses.Failed;
        item.LastAnalyzedAt = null;
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "Reaper abandoned job {JobId} for portfolio item {PortfolioItemId}; mirrored AnalysisStatus=Failed.",
            job.Id, portfolioItemId);
    }

    /// <summary>
    /// Try to claim one <c>Pending</c> job of type <see cref="PortfolioJobTypes.AnalyzePortfolioItem"/>
    /// and process it to a terminal state. Returns <see cref="AnalysisTickOutcome.NoWork"/>
    /// when no <c>Pending</c> job is visible; <see cref="AnalysisTickOutcome.ClaimedByAnother"/>
    /// when the atomic UPDATE matched zero rows (a concurrent tick beat us); and
    /// <see cref="AnalysisTickOutcome.Processed"/> when this call did the work.
    /// </summary>
    /// <remarks>
    /// STOR-40 Phase 1: the polling worker runs outside any HTTP request, so the ambient
    /// account is never populated by the middleware. The EF Core global query filter, the
    /// <see cref="Persistence.Interceptors.RowLevelSecurityInterceptor"/> save-time guard,
    /// and the Postgres RLS policy all key on the ambient account — running them with
    /// <c>AccountId = null</c> hides every <c>Pending</c> row from the claim SELECT and
    /// trips the save-time guard the moment we try to write findings. We bracket this
    /// method with two scopes, sequential not nested: the system scope covers the claim
    /// SELECT/UPDATE only; once we have a job, we close the system scope and reopen under
    /// <c>BeginAccountScope(job.AccountId)</c> for the rest.
    /// </remarks>
    public async Task<BackgroundJobTickOutcome> TryProcessOneAsync(CancellationToken cancellationToken)
    {
        // The InMemory branch keeps its own claim mechanics (synchronous
        // SaveChanges inside a static lock) and runs the per-account work under
        // the same BeginAccountScope at the end of this method — same plumbing,
        // two different code paths.
        var isInMemoryProvider = _dbContext.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true;

        // Step 1: claim under the system scope. The scope's IsAdministrator = true
        // short-circuits the EF global query filter; the RLS interceptor writes
        // app.is_admin=true on every command so the Postgres policy's USING clause
        // matches every row. Once we know which job (if any) was claimed we exit
        // the scope and run the per-account work under BeginAccountScope.
        Job? job = null;
        BackgroundJobTickOutcome claimOutcome;
        DateTime nowUtc;

        using (_accountScope.BeginSystemScope())
        {
            nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
            if (isInMemoryProvider)
            {
                (claimOutcome, job) = await ClaimOneInMemoryAsync(nowUtc).ConfigureAwait(false);
            }
            else
            {
                (claimOutcome, job) = await ClaimOnePostgresAsync(nowUtc, cancellationToken).ConfigureAwait(false);
            }
        }

        if (claimOutcome != BackgroundJobTickOutcome.Processed || job is null)
        {
            return claimOutcome;
        }

        // Step 2: post-claim work (item load, LLM call, findings write, status
        // updates) all runs under the owning account's scope so the global query
        // filter, save-time guard, and RLS policy all key on the correct
        // account. The scope restores the prior ambient context on dispose,
        // including on exceptions.
        using (_accountScope.BeginAccountScope(job.AccountId))
        {
            return await CompleteProcessingOrRollForwardAsync(job, nowUtc, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Atomic Postgres claim: select one Pending row, then flip it to Running via a
    /// single <c>UPDATE ... WHERE Status = 'Pending'</c>. Returns <c>(NoWork, null)</c>
    /// when no Pending row is visible and <c>(ClaimedByAnother, null)</c> when a
    /// concurrent tick beat us to it; <c>(Processed, job)</c> otherwise. Mirrors the
    /// pre-Phase-1 branch verbatim — see the inline comment block in
    /// <see cref="TryProcessOneAsync"/>'s older revision for the full reasoning.
    /// </summary>
    private async Task<(BackgroundJobTickOutcome Outcome, Job? Job)> ClaimOnePostgresAsync(
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        var pendingJobId = await _dbContext.Jobs
            .Where(job => job.Type == PortfolioJobTypes.AnalyzePortfolioItem && job.Status == JobStatus.Pending)
            .OrderBy(job => job.CreatedAt)
            .Select(job => job.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (pendingJobId == Guid.Empty)
        {
            return (BackgroundJobTickOutcome.NoWork, null);
        }

        var claimedRows = await _dbContext.Jobs
            .Where(job => job.Id == pendingJobId
                && job.Status == JobStatus.Pending
                && job.Type == PortfolioJobTypes.AnalyzePortfolioItem)
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(job => job.Status, JobStatus.Running)
                    .SetProperty(job => job.StartedAt, nowUtc)
                    .SetProperty(job => job.UpdatedAt, nowUtc),
                cancellationToken)
            .ConfigureAwait(false);

        if (claimedRows == 0)
        {
            return (BackgroundJobTickOutcome.ClaimedByAnother, null);
        }

        var job = await _dbContext.Jobs
            .AsNoTracking()
            .FirstAsync(j => j.Id == pendingJobId, cancellationToken)
            .ConfigureAwait(false);

        return (BackgroundJobTickOutcome.Processed, job);
    }

    /// <summary>
    /// InMemory-only claim. The unit-test path uses EF InMemory, which doesn't
    /// support <c>ExecuteUpdateAsync</c> — fall back to a load-then-recheck pattern
    /// serialized through <see cref="InMemoryClaimLock"/>. The system scope's
    /// IsAdministrator short-circuit makes the EF filter a no-op so the InMemory
    /// load sees every Pending row (the same way Postgres sees them under the
    /// system scope's GUCs).
    /// </summary>
    private Task<(BackgroundJobTickOutcome Outcome, Job? Job)> ClaimOneInMemoryAsync(DateTime nowUtc)
    {
        Job? job;
        BackgroundJobTickOutcome outcome;
        lock (InMemoryClaimLock)
        {
            _dbContext.ChangeTracker.Clear();

            var candidate = _dbContext.Jobs
                .Where(j => j.Type == PortfolioJobTypes.AnalyzePortfolioItem && j.Status == JobStatus.Pending)
                .OrderBy(j => j.CreatedAt)
                .FirstOrDefault();

            if (candidate is null)
            {
                return Task.FromResult((BackgroundJobTickOutcome.NoWork, (Job?)null));
            }

            var recheck = _dbContext.Jobs
                .AsNoTracking()
                .Where(j => j.Id == candidate.Id && j.Status == JobStatus.Pending)
                .Select(j => new { j.Id })
                .FirstOrDefault();

            if (recheck is null)
            {
                return Task.FromResult((BackgroundJobTickOutcome.ClaimedByAnother, (Job?)null));
            }

            candidate.Status = JobStatus.Running;
            candidate.StartedAt = nowUtc;
            candidate.UpdatedAt = nowUtc;
            _dbContext.SaveChanges();

            job = _dbContext.Jobs
                .AsNoTracking()
                .First(j => j.Id == candidate.Id);
            outcome = BackgroundJobTickOutcome.Processed;
        }

        _dbContext.ChangeTracker.Clear();
        return Task.FromResult((outcome, (Job?)job));
    }

    /// <summary>
    /// Static monitor that serializes the "load candidate -> recheck -> flip to
    /// Running" sequence the InMemory branch uses. The InMemory provider is
    /// in-process and single-threaded for the load + recheck window, but the
    /// await points in <see cref="FirstOrDefaultAsync"/> let another tick slip
    /// through; the static lock compensates for that without affecting the
    /// production Postgres path (which has its own atomic UPDATE WHERE).
    /// </summary>
    private static readonly object InMemoryClaimLock = new();

    private async Task<BackgroundJobTickOutcome> CompleteProcessingOrRollForwardAsync(
        Job job,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        try
        {
            await ProcessClaimedJobAsync(job, nowUtc, cancellationToken).ConfigureAwait(false);
            return BackgroundJobTickOutcome.Processed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Worker shutdown: leave the job in Running so an operator can inspect
            // it; re-marking Pending would let the next worker re-claim and
            // potentially double-run the LLM call on the way out.
            throw;
        }
        catch (Exception ex)
        {
            // Anything outside the LLM/JSON retry envelope — defensive guard so a
            // bug in the processor itself can't spin the worker in a tight loop.
            // Mirrors the LLM/JSON retry path: count, retry until MaxAttempts, then
            // mark Failed. The portfolio item id may not have been resolved by the
            // time the bug blew up, so it's passed as null — the item-status mirror
            // is skipped in that case (nothing to flip).
            _logger.LogError(ex, "Unexpected error processing job {JobId}; treating as a retryable failure.", job.Id);
            await HandleRetryableFailureAsync(job, nowUtc, portfolioItemId: null,
                "Internal processor error: " + ex.Message, cancellationToken)
                .ConfigureAwait(false);
            return BackgroundJobTickOutcome.Processed;
        }
    }

    private async Task ProcessClaimedJobAsync(
        Job job,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (!TryDeserializePayload(job.PayloadJson, out var portfolioItemId))
        {
            // A malformed payload is not a transient failure — no amount of
            // retrying will fix a JSON we can't parse. Mark Failed immediately
            // with a clear error message; the item stays at NotAnalyzed (we have
            // no PortfolioItem to flip).
            await FailJobAsync(job, nowUtc, portfolioItemId: null,
                "Job payload was not a valid AnalyzePortfolioItem payload.", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Load the portfolio item under the same account-scoped filter the rest
        // of the codebase uses. A claim racing against a delete (or against a
        // mis-accounted enqueue) sees no row here and we mark the job Failed —
        // no point retrying a job whose item is gone.
        var portfolioItem = await _dbContext.PortfolioItems
            .FirstOrDefaultAsync(item => item.Id == portfolioItemId, cancellationToken)
            .ConfigureAwait(false);

        if (portfolioItem is null)
        {
            await FailJobAsync(job, nowUtc, portfolioItemId,
                $"Referenced portfolio item '{portfolioItemId}' was not found.",
                cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var extraction = await _extractor
            .ExtractAsync(portfolioItem, cancellationToken)
            .ConfigureAwait(false);

        if (extraction.Outcome == ExtractionOutcome.Unsupported)
        {
            // Unsupported short-circuit: the evidence type has no extractable text
            // path so no LLM call will ever happen. Skip the optimistic Analyzing
            // flip and go straight from the prior status to Unsupported in one
            // write — never passing through Analyzing means no extra DB round-trip
            // and no extra audit-log row for the unsupported branch.
            portfolioItem.AnalysisStatus = PortfolioAnalysisStatuses.Unsupported;
            portfolioItem.LastAnalyzedAt = null;
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            await CompleteJobAsync(job, nowUtc, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Job {JobId} for portfolio item {PortfolioItemId} short-circuited to Unsupported (no LLM call).",
                job.Id, portfolioItem.Id);
            return;
        }

        // Text-extractable path: optimistically mark the item as Analyzing so the
        // UI's status badge updates between submission and the (potentially slow)
        // LLM call. This is the only write to AnalysisStatus that doesn't correspond
        // to a terminal outcome.
        if (portfolioItem.AnalysisStatus != PortfolioAnalysisStatuses.Analyzing)
        {
            portfolioItem.AnalysisStatus = PortfolioAnalysisStatuses.Analyzing;
            await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        // Text outcome: build the prompt, call the LLM, parse the JSON, write
        // the findings. LlmProviderException / JSON parse failure both flow into
        // the retry path.
        var completionRequest = PortfolioAnalysisPromptBuilder
            .BuildCompletionRequest(portfolioItem, extraction.Text!);

        LlmCompletionResult completion;
        try
        {
            completion = await _llmClient
                .CompleteAsync(completionRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LlmProviderException ex)
        {
            await HandleRetryableFailureAsync(job, nowUtc, portfolioItemId, "LLM provider error: " + ex.Message, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        IReadOnlyList<PortfolioSkillFindingDto> findings;
        try
        {
            findings = ParseFindingsJson(completion.OutputText);
        }
        catch (JsonException ex)
        {
            await HandleRetryableFailureAsync(job, nowUtc, portfolioItemId, "LLM response was not valid JSON: " + ex.Message, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // The new shared LlmJsonExtractor is more forgiving than the old
        // private ExtractJsonArraySlice — it skips brackets that appear inside
        // string values, which the local Gemma reasoning model sometimes
        // emits. The portfolio analyzer still expects exactly an array, so we
        // branch on the slice kind here rather than letting the serializer
        // guess.

        // Replace any prior findings for this item — a successful analysis
        // overwrites, it doesn't append. The Phase 2 worker's "delete prior +
        // insert new" choice keeps the table small and avoids a separate
        // history story.
        var existing = await _dbContext.PortfolioSkillFindings
            .Where(f => f.PortfolioItemId == portfolioItem.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (existing.Count > 0)
        {
            _dbContext.PortfolioSkillFindings.RemoveRange(existing);
        }

        var findingsCreatedAt = _timeProvider.GetUtcNow();
        foreach (var dto in findings)
        {
            if (!IsKnownBand(dto.Band))
            {
                // A bad band is a model-output regression, not a transient
                // failure: a retry will hit the same model and may return the
                // same band. Treat the whole batch as a non-retryable failure
                // so the item lands on Failed with a clear message rather than
                // burning retries.
                await FailJobAsync(job, nowUtc, portfolioItemId,
                    $"LLM returned an unknown confidence band '{dto.Band}'.",
                    cancellationToken)
                    .ConfigureAwait(false);
                return;
            }

            _dbContext.PortfolioSkillFindings.Add(new PortfolioSkillFinding
            {
                Id = Guid.NewGuid(),
                AccountId = portfolioItem.AccountId,
                PortfolioItemId = portfolioItem.Id,
                SkillName = dto.Skill,
                ConfidenceBand = dto.Band,
                Explanation = dto.Explanation,
                CreatedAt = findingsCreatedAt,
            });
        }

        portfolioItem.AnalysisStatus = PortfolioAnalysisStatuses.Analyzed;
        portfolioItem.LastAnalyzedAt = findingsCreatedAt;

        // STOR-43 Phase 1: enqueue a talent-index refresh job in the same
        // SaveChanges as the Analyzed flip. The helper short-circuits to
        // Guid.Empty when the student isn't opted in, so non-searchable
        // students incur no extra DB write beyond the lookup. The new job
        // row is added to the change tracker here and committed atomically
        // with the status flip below — a half-committed state
        // (Analyzed + no refresh job for a searchable student) is
        // structurally impossible.
        await TalentIndexRefreshJobs
            .EnqueueIfSearchableAsync(
                _dbContext,
                portfolioItem.AccountId,
                nowUtc,
                cancellationToken)
            .ConfigureAwait(false);

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await CompleteJobAsync(job, nowUtc, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Job {JobId} for portfolio item {PortfolioItemId} succeeded with {FindingCount} skill finding(s).",
            job.Id, portfolioItem.Id, findings.Count);
    }

    private static bool TryDeserializePayload(string payloadJson, out Guid portfolioItemId)
    {
        portfolioItemId = Guid.Empty;
        try
        {
            var payload = JsonSerializer.Deserialize<AnalyzePortfolioItemPayload>(payloadJson, JsonOptions);
            if (payload is null)
            {
                return false;
            }

            portfolioItemId = payload.PortfolioItemId;
            return portfolioItemId != Guid.Empty;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static IReadOnlyList<PortfolioSkillFindingDto> ParseFindingsJson(string outputText)
    {
        if (string.IsNullOrWhiteSpace(outputText))
        {
            throw new JsonException("LLM returned an empty response body.");
        }

        // STOR-40: route the LLM output through the shared, string-aware
        // LlmJsonExtractor so brackets that appear inside string values
        // (a known quirk of the local Gemma reasoning model) do not end
        // the slice early. The portfolio analyzer expects an array;
        // the advisor will expect an object — LlmJsonExtractor reports the
        // kind so each caller can deserialize with the matching shape.
        var slice = LlmJsonExtractor.ExtractJsonDocument(outputText)
            ?? throw new JsonException("LLM output contained no JSON array.");

        if (slice.Kind != ExtractedJsonDocumentKind.Array)
        {
            throw new JsonException(
                $"LLM output was a JSON object; the portfolio analyzer requires an array. Got: {slice.Text[..Math.Min(slice.Text.Length, 64)]}...");
        }

        var parsed = JsonSerializer.Deserialize<List<PortfolioSkillFindingDto>>(slice.Text, JsonOptions)
            ?? throw new JsonException("LLM returned a JSON null body.");

        // Validate required fields are non-empty (the JSON serializer would
        // happily accept nulls for reference-type fields).
        foreach (var finding in parsed)
        {
            if (string.IsNullOrWhiteSpace(finding.Skill)
                || string.IsNullOrWhiteSpace(finding.Band)
                || string.IsNullOrWhiteSpace(finding.Explanation))
            {
                throw new JsonException(
                    "LLM JSON contained a finding with empty skill, band, or explanation.");
            }
        }

        return parsed;
    }

    private static bool IsKnownBand(string band) =>
        string.Equals(band, ConfidenceBands.Strong, StringComparison.Ordinal)
        || string.Equals(band, ConfidenceBands.Developing, StringComparison.Ordinal)
        || string.Equals(band, ConfidenceBands.Missing, StringComparison.Ordinal);

    private async Task CompleteJobAsync(Job job, DateTime nowUtc, CancellationToken cancellationToken)
    {
        // The job came in via AsNoTracking for the in-process payload read above;
        // re-attach it for this final status write so the change tracker writes
        // back the transition (Running -> Succeeded) with no full SELECT.
        _dbContext.Jobs.Attach(job);
        job.Status = JobStatus.Succeeded;
        job.CompletedAt = nowUtc;
        job.UpdatedAt = nowUtc;
        job.ErrorMessage = null;
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FailJobAsync(
        Job job,
        DateTime nowUtc,
        Guid? portfolioItemId,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        _dbContext.Jobs.Attach(job);
        job.Status = JobStatus.Failed;
        job.ErrorMessage = errorMessage;
        job.CompletedAt = nowUtc;
        job.UpdatedAt = nowUtc;
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Mirror the job's terminal status onto the portfolio item when we know
        // which item the job was for. The caller passes null when the payload
        // couldn't be deserialized (no PortfolioItem to flip) — that's the
        // "bad-payload" path, which the original implementation hit by falling
        // back to a second TryDeserializePayload round-trip.
        if (portfolioItemId.HasValue)
        {
            var item = await _dbContext.PortfolioItems
                .FirstOrDefaultAsync(i => i.Id == portfolioItemId.Value, cancellationToken)
                .ConfigureAwait(false);
            if (item is not null)
            {
                item.AnalysisStatus = PortfolioAnalysisStatuses.Failed;
                item.LastAnalyzedAt = null;
                await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleRetryableFailureAsync(
        Job job,
        DateTime nowUtc,
        Guid? portfolioItemId,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        var nextAttempt = job.AttemptCount + 1;
        _dbContext.Jobs.Attach(job);
        job.AttemptCount = nextAttempt;
        job.ErrorMessage = errorMessage;
        job.UpdatedAt = nowUtc;

        if (nextAttempt < MaxAttempts)
        {
            // Re-queue for another tick. Status back to Pending (not Running) so
            // the row-claim UPDATE matches it again on the next pass.
            job.Status = JobStatus.Pending;
            job.StartedAt = null;
            job.CompletedAt = null;
            _logger.LogWarning(
                "Job {JobId} attempt {Attempt}/{Max} failed ({Error}); re-queuing.",
                job.Id, nextAttempt, MaxAttempts, errorMessage);
        }
        else
        {
            job.Status = JobStatus.Failed;
            job.CompletedAt = nowUtc;
            _logger.LogError(
                "Job {JobId} failed after {Max} attempts: {Error}",
                job.Id, MaxAttempts, errorMessage);
        }

        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Mirror the terminal Failed status onto the portfolio item so the UI
        // can show the Retry button. Only mirror on the failure-path terminal
        // case — on a re-queue the item stays at Analyzing (the optimistic
        // flip from earlier in ProcessClaimedJobAsync). portfolioItemId is null
        // for the defensive "unhandled exception" catch in
        // CompleteProcessingOrRollForwardAsync — that branch has no item to
        // flip, so the mirror is skipped.
        if (job.Status == JobStatus.Failed && portfolioItemId.HasValue)
        {
            var item = await _dbContext.PortfolioItems
                .FirstOrDefaultAsync(i => i.Id == portfolioItemId.Value, cancellationToken)
                .ConfigureAwait(false);
            if (item is not null)
            {
                item.AnalysisStatus = PortfolioAnalysisStatuses.Failed;
                item.LastAnalyzedAt = null;
                await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
