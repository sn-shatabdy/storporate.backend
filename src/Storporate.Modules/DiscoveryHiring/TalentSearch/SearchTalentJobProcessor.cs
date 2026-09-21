using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Jobs;
using Storporate.Infrastructure.Llm;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.DiscoveryHiring.TalentSearch;

/// <summary>
/// Processes a single <see cref="Job"/> of type
/// <see cref="DiscoveryJobTypes.SearchTalent"/> end-to-end: claim the job,
/// load the owning <see cref="TalentSearchRequest"/> under the job's
/// account scope, run the requirements-extraction and ranking LLM calls,
/// validate every citation against the live <see cref="TalentIndexEntry"/>
/// snapshot, write the result JSON, and complete (or fail) the request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two-scope bracket.</b> Claim runs under
/// <see cref="IBackgroundAccountScope.BeginSystemScope"/>; the rest runs
/// under <c>BeginAccountScope(job.AccountId)</c> — same pattern as the
/// Phase 1 <c>RefreshTalentIndexEntryProcessor</c>. Crucially, the
/// search job reads only <see cref="TalentIndexEntry"/> (which is NOT
/// <see cref="IAccountScoped"/>) and the <see cref="TalentSearchRequest"/>
/// itself. The job cannot read any student <see cref="PortfolioItem"/> or
/// <see cref="PortfolioSkillFinding"/> row (the global query filter +
/// RLS block both reads); that is the design, and the live test in
/// <c>TalentSearchPostgresTests</c> proves the isolation still holds under
/// the restricted <c>storporate_app</c> role.
/// </para>
/// <para>
/// <b>Pipeline.</b>
/// <list type="number">
///   <item>Requirements LLM (MaxOutputTokens 512) extracts skills +
///   summary; invalid JSON / empty / <see cref="LlmProviderException"/>
///   falls back to the raw query and continues — the search still
///   completes.</item>
///   <item>Embedding call on query + (optional) "Skills: ..." tail,
///   capped at <see cref="TalentSearchDefaults.EmbeddingInputMaxCharacters"/>.
///   LLM failures retry per the job-bookkeeper path; exhaustion fails
///   the request with <c>llm_provider_error</c>.</item>
///   <item><see cref="ITalentIndexRepository.SearchNearestAsync"/> pulls
///   the top <see cref="TalentSearchDefaults.CandidatePoolSize"/> =
///   20 candidates by cosine distance.</item>
///   <item>Zero candidates → Completed with empty results, no ranking
///   call.</item>
///   <item>Ranking LLM (MaxOutputTokens 4096) returns the strict-JSON
///   ranking array. The parser validates every citation against the
///   candidate's snapshot, drops unknown handles and duplicates, and
///   substitutes the deterministic fallback for any reason that
///   mentions <c>evidence</c> / <c>proof</c>, is empty, or has zero
///   valid citations.</item>
///   <item>Unranked candidates are appended in vector order; the whole
///   list is trimmed to <see cref="TalentSearchDefaults.ResultLimit"/> =
///   10 and serialized to <see cref="TalentSearchRequest.ResultJson"/>.</item>
/// </list>
/// </para>
/// <para>
/// <b>Audit.</b> Two audit rows are written — one on Completed (with
/// <c>resultCount</c>) and one on Failed (with <c>errorCode</c>). The
/// <c>POST</c> endpoint already wrote the <c>talent_search_requested</c>
/// row with the hash + length. Audit failures never change the job
/// outcome (the writer already swallows errors, same shape as the
/// advisor processor).
/// </para>
/// </remarks>
public sealed class SearchTalentJobProcessor : IBackgroundJobProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly WriteDbContext _dbContext;
    private readonly ITalentIndexRepository _repository;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly ILlmClient _llmClient;
    private readonly IAuditLogWriter _auditLogWriter;
    private readonly ILogger<SearchTalentJobProcessor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IBackgroundAccountScope _accountScope;

    public SearchTalentJobProcessor(
        WriteDbContext dbContext,
        ITalentIndexRepository repository,
        IEmbeddingClient embeddingClient,
        ILlmClient llmClient,
        IAuditLogWriter auditLogWriter,
        ILogger<SearchTalentJobProcessor> logger,
        TimeProvider timeProvider,
        IBackgroundAccountScope accountScope)
    {
        _dbContext = dbContext;
        _repository = repository;
        _embeddingClient = embeddingClient;
        _llmClient = llmClient;
        _auditLogWriter = auditLogWriter;
        _logger = logger;
        _timeProvider = timeProvider;
        _accountScope = accountScope;
    }

    /// <inheritdoc />
    public string JobType => DiscoveryJobTypes.SearchTalent;

    /// <inheritdoc />
    /// <remarks>
    /// Mirrors the abandoned-job disposition onto the parent
    /// <see cref="TalentSearchRequest"/> so the API's status flips in
    /// lockstep with the job. OnJobAbandonedAsync must be invoked under the
    /// job's account scope — <c>PortfolioAnalysisWorker.MirrorAbandonedJobsAsync</c>
    /// opens <c>BeginAccountScope(job.AccountId)</c> before calling here, and
    /// the EF global query filter on <see cref="IAccountScoped"/> would
    /// otherwise hide the parent <see cref="TalentSearchRequest"/> row.
    /// </remarks>
    public async Task OnJobAbandonedAsync(Job job, CancellationToken cancellationToken)
    {
        if (!TryDeserializePayload(job.PayloadJson, out var searchId))
        {
            return;
        }

        var request = await _dbContext.TalentSearchRequests
            .FirstOrDefaultAsync(r => r.Id == searchId, cancellationToken)
            .ConfigureAwait(false);
        if (request is null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        request.Status = TalentSearchStatuses.Failed;
        request.ErrorCode = "llm_provider_error";
        request.CompletedAt = now;
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await _auditLogWriter.WriteAsync(
            action: "talent_search_failed",
            resourceType: "TalentSearch",
            resourceId: request.Id.ToString(),
            metadataJson: JsonSerializer.Serialize(new { errorCode = "llm_provider_error" }, JsonOptions),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "Reaper abandoned job {JobId} for search {SearchId}; mirrored Status=Failed.",
            job.Id, searchId);
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
                DiscoveryJobTypes.SearchTalent,
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
                _logger.LogError(ex, "Unexpected error processing search job {JobId}; treating as retryable.", job.Id);
                // The exception message can echo a provider's input — log it
                // (already done above) but don't include it in the job
                // failure reason / audit chain.
                await JobBookkeeper.RequeueOrFailAsync(
                    _dbContext, job, nowUtc, "Internal processor error.",
                    _logger, cancellationToken).ConfigureAwait(false);
                return BackgroundJobTickOutcome.Processed;
            }
        }
    }

    private async Task ProcessClaimedJobAsync(Job job, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (!TryDeserializePayload(job.PayloadJson, out var searchId))
        {
            await JobBookkeeper.MarkFailedAsync(
                _dbContext, job, nowUtc,
                "Job payload was not a valid SearchTalent payload.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var request = await _dbContext.TalentSearchRequests
            .FirstOrDefaultAsync(r => r.Id == searchId, cancellationToken)
            .ConfigureAwait(false);
        if (request is null)
        {
            await JobBookkeeper.MarkFailedAsync(
                _dbContext, job, nowUtc,
                $"Referenced talent search '{searchId}' was not found.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var query = request.QueryText;

        // ---- 1. Requirements extraction (graceful fallback). ----
        SearchTalentRequirementsParser.SearchTalentExtractedRequirements requirements;
        try
        {
            var request1 = SearchTalentRequirementsPromptBuilder.BuildCompletionRequest(
                new SearchTalentRequirementsInputs(query));
            var completion1 = await _llmClient.CompleteAsync(request1, cancellationToken)
                .ConfigureAwait(false);
            requirements = SearchTalentRequirementsParser.Parse(completion1.OutputText);
        }
        catch (Exception ex) when (ex is LlmProviderException
                                    or SearchTalentResponseInvalidException)
        {
            _logger.LogInformation(
                "Search {SearchId}: requirements extraction fell back to raw query ({Reason}).",
                searchId, ex.Message);
            requirements = new SearchTalentRequirementsParser.SearchTalentExtractedRequirements(
                Skills: Array.Empty<string>(),
                Summary: null);
        }

        // ---- 2. Embedding. ----
        float[] embedding;
        try
        {
            var embeddingInput = BuildEmbeddingInput(query, requirements.Skills);
            var vectors = await _embeddingClient
                .EmbedAsync(new[] { embeddingInput }, cancellationToken)
                .ConfigureAwait(false);
            embedding = vectors[0];
        }
        catch (LlmProviderException ex)
        {
            _logger.LogInformation(
                "Search {SearchId}: embedding provider failed ({Reason}).",
                searchId, ex.Message);
            var outcome = await JobBookkeeper.RequeueOrFailAsync(
                _dbContext, job, nowUtc, "Embedding provider error.",
                _logger, cancellationToken).ConfigureAwait(false);
            if (outcome == JobBookkeeper.RequeueOrFailOutcome.Failed)
            {
                await MarkSearchTerminalFailedAsync(
                    request, "llm_provider_error", cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        // ---- 3. Retrieval. ----
        IReadOnlyList<TalentIndexSearchHit> hits;
        try
        {
            hits = await _repository
                .SearchNearestAsync(embedding, TalentSearchDefaults.CandidatePoolSize, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (LlmProviderException ex)
        {
            // SearchNearestAsync throws LlmProviderException only on a wrong-dimension
            // query (the repository does no other LLM calls). Treat as provider failure.
            _logger.LogInformation(
                "Search {SearchId}: embedding dimension error ({Reason}).",
                searchId, ex.Message);
            var outcome = await JobBookkeeper.RequeueOrFailAsync(
                _dbContext, job, nowUtc, "Embedding dimension error.",
                _logger, cancellationToken).ConfigureAwait(false);
            if (outcome == JobBookkeeper.RequeueOrFailOutcome.Failed)
            {
                await MarkSearchTerminalFailedAsync(
                    request, "llm_provider_error", cancellationToken).ConfigureAwait(false);
            }
            return;
        }

        // ---- 4. Zero candidates → Completed with empty results, no ranking call. ----
        if (hits.Count == 0)
        {
            await CompleteAsync(
                request, job, nowUtc, Array.Empty<SearchTalentValidatedCandidate>(), cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        // Load the matching TalentIndexEntries by id, in the repository's
        // vector-order. Use AsNoTracking + IgnoreQueryFilters because
        // TalentIndexEntry is NOT IAccountScoped but we still want the
        // query plan to skip the global filter loop's overhead.
        var entryIds = hits.Select(h => h.EntryId).ToHashSet();
        var entries = await _dbContext.TalentIndexEntries
            .AsNoTracking()
            .IgnoreQueryFilters()
            .Where(e => entryIds.Contains(e.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var entriesById = entries.ToDictionary(e => e.Id);
        var orderedEntries = hits
            .Select(h => entriesById.TryGetValue(h.EntryId, out var e) ? e : null)
            .Where(e => e is not null)
            .Cast<TalentIndexEntry>()
            .ToList();

        var handles = orderedEntries
            .Select((_, i) => (Handle: $"c{i + 1}", Entry: orderedEntries[i]))
            .ToList();
        var candidateHandles = handles.ToDictionary(
            h => h.Handle,
            h => BuildCandidateSnapshot(h.Entry));

        // ---- 5. Ranking + server-side validation. ----
        IReadOnlyList<SearchTalentValidatedCandidate> validated;
        try
        {
            var request2 = SearchTalentRankingPromptBuilder.BuildCompletionRequest(
                new SearchTalentRankingInputs(query, candidateHandles.Values.ToList()));
            var completion2 = await _llmClient.CompleteAsync(request2, cancellationToken)
                .ConfigureAwait(false);
            validated = SearchTalentRankingParser.Parse(
                completion2.OutputText, candidateHandles, _logger);
        }
        catch (Exception ex) when (ex is LlmProviderException
                                    or SearchTalentResponseInvalidException)
        {
            _logger.LogInformation(
                "Search {SearchId}: ranking call failed ({Reason}); falling back to vector order with deterministic reasons.",
                searchId, ex.Message);
            validated = handles
                .Select(h => BuildDeterministicCandidate(h.Entry, candidateHandles[h.Handle]))
                .ToList();
        }

        // Append unranked tail in vector order, then trim to ResultLimit.
        // `seenEntryIds` guards against the (rare) case where the LLM
        // emits a duplicate entry id — the parser already drops duplicate
        // handles, but a degenerate model could still repeat an entry id
        // under different handles if the prompt went off-script.
        var finalList = new List<SearchTalentValidatedCandidate>(capacity: TalentSearchDefaults.ResultLimit);
        var seenEntryIds = new HashSet<Guid>();
        foreach (var v in validated)
        {
            if (finalList.Count >= TalentSearchDefaults.ResultLimit)
            {
                break;
            }
            if (seenEntryIds.Add(v.CandidateId))
            {
                finalList.Add(v);
            }
        }
        foreach (var h in handles)
        {
            if (finalList.Count >= TalentSearchDefaults.ResultLimit)
            {
                break;
            }
            if (!seenEntryIds.Contains(h.Entry.Id))
            {
                finalList.Add(BuildDeterministicCandidate(h.Entry, candidateHandles[h.Handle]));
                seenEntryIds.Add(h.Entry.Id);
            }
        }

        await CompleteAsync(
            request, job, nowUtc, finalList, cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteAsync(
        TalentSearchRequest request,
        Job job,
        DateTime nowUtc,
        IReadOnlyList<SearchTalentValidatedCandidate> validated,
        CancellationToken cancellationToken)
    {
        // Project the validated candidates into the wire-shape DTOs the
        // GET endpoint will return. We do this projection NOW (rather
        // than in the GET handler) because the snapshot the candidates
        // point to may have changed by the time the FE polls — the GET
        // handler's re-validation step is the safety net.
        var resultItems = validated.Select(v => new TalentSearchResultItem(
            CandidateId: v.CandidateId,
            DisplayName: string.Empty, // refilled by GET re-validation
            Headline: null,
            University: null,
            FieldOfStudy: null,
            StudyYear: null,
            MatchedSkills: v.MatchedSkills
                .Select(ms => new TalentSearchMatchedSkill(ms.Name, ms.Band))
                .ToList(),
            Reason: v.Reason,
            CitedItems: v.CitedItems
                .Select(ci => new TalentSearchCitedItem(
                    ci.PortfolioItemId, ci.Label, ci.Category, ci.SkillName, ci.Band))
                .ToList())).ToList();

        var resultJson = JsonSerializer.Serialize(resultItems, JsonOptions);
        var now = _timeProvider.GetUtcNow();
        request.Status = TalentSearchStatuses.Completed;
        request.CompletedAt = now;
        request.ResultJson = resultJson;
        request.ErrorCode = null;
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        await JobBookkeeper.MarkSucceededAsync(_dbContext, job, nowUtc, cancellationToken)
            .ConfigureAwait(false);

        var auditMetadata = JsonSerializer.Serialize(new { resultCount = resultItems.Count }, JsonOptions);
        await _auditLogWriter.WriteAsync(
            action: "talent_search_completed",
            resourceType: "TalentSearch",
            resourceId: request.Id.ToString(),
            metadataJson: auditMetadata,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Search {SearchId}: completed with {ResultCount} result(s).",
            request.Id, resultItems.Count);
    }

    private async Task MarkSearchTerminalFailedAsync(
        TalentSearchRequest request,
        string errorCode,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        request.Status = TalentSearchStatuses.Failed;
        request.ErrorCode = errorCode;
        request.CompletedAt = now;
        request.ResultJson = null;
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var metadata = JsonSerializer.Serialize(new { errorCode }, JsonOptions);
        await _auditLogWriter.WriteAsync(
            action: "talent_search_failed",
            resourceType: "TalentSearch",
            resourceId: request.Id.ToString(),
            metadataJson: metadata,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "Search {SearchId}: failed with errorCode={ErrorCode}.",
            request.Id, errorCode);
    }

    private static string BuildEmbeddingInput(string query, IReadOnlyList<string> skills)
    {
        var sb = new StringBuilder(capacity: 256);
        sb.Append(query);
        if (skills.Count > 0)
        {
            sb.Append('\n').Append("Skills: ");
            sb.Append(string.Join(", ", skills));
        }
        var text = sb.ToString();
        if (text.Length > TalentSearchDefaults.EmbeddingInputMaxCharacters)
        {
            text = text[..TalentSearchDefaults.EmbeddingInputMaxCharacters];
        }
        return text;
    }

    /// <summary>Convert a <see cref="TalentIndexEntry"/> into the
    /// prompt-shape candidate snapshot. Only structured fields enter the
    /// snapshot — display name, headline, university, field of study,
    /// year are deliberately excluded so an attacker cannot inject
    /// free-text content via crafted skill labels.</summary>
    private static SearchTalentRankingCandidate BuildCandidateSnapshot(TalentIndexEntry entry)
    {
        var itemsJson = entry.ItemsJson;
        IReadOnlyList<TalentIndexItemSnapshot> items;
        try
        {
            items = JsonSerializer.Deserialize<List<TalentIndexItemSnapshot>>(itemsJson, JsonOptions)
                ?? new List<TalentIndexItemSnapshot>();
        }
        catch (JsonException)
        {
            // The snapshot is corrupted (should never happen — the refresh
            // processor writes it). Skip this candidate rather than throw —
            // the search still completes for the rest of the pool.
            items = Array.Empty<TalentIndexItemSnapshot>();
        }

        var rankingItems = items.Select(i => new SearchTalentRankingItem(
            PortfolioItemId: i.PortfolioItemId,
            Label: i.Label,
            Category: i.Category,
            Skills: i.Skills.Select(s => new SearchTalentRankingSkill(s.Name, s.Band)).ToList()))
            .ToList();

        return new SearchTalentRankingCandidate(
            EntryId: entry.Id,
            Items: rankingItems);
    }

    /// <summary>Build a deterministic candidate when the ranking LLM
    /// failed: pull every Strong/Developing skill from the snapshot, keep
    /// up to 10, write a vector-order reason. Used for the unranked-tail
    /// fallback and the whole-list fallback when the ranking call
    /// threw.</summary>
    private static SearchTalentValidatedCandidate BuildDeterministicCandidate(
        TalentIndexEntry entry,
        SearchTalentRankingCandidate snapshot)
    {
        var matchedSkills = snapshot.Items
            .SelectMany(i => i.Skills
                .Where(s => string.Equals(s.Band, ConfidenceBands.Strong, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(s.Band, ConfidenceBands.Developing, StringComparison.OrdinalIgnoreCase))
                .Select(s => new MatchedSkillBand(s.Name, s.Band)))
            .Distinct()
            .OrderByDescending(s => string.Equals(s.Band, ConfidenceBands.Strong, StringComparison.OrdinalIgnoreCase))
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        var citedItems = new List<ValidatedCitedItem>();
        foreach (var skill in matchedSkills)
        {
            foreach (var item in snapshot.Items)
            {
                var matchedSkill = item.Skills.FirstOrDefault(s =>
                    string.Equals(s.Name, skill.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.Band, skill.Band, StringComparison.OrdinalIgnoreCase));
                if (matchedSkill is null)
                {
                    continue;
                }
                citedItems.Add(new ValidatedCitedItem(
                    PortfolioItemId: item.PortfolioItemId,
                    Label: item.Label,
                    Category: item.Category,
                    SkillName: matchedSkill.Name,
                    Band: matchedSkill.Band));
                break; // first item per skill
            }
            if (citedItems.Count >= 3)
            {
                break;
            }
        }

        var reason = BuildDeterministicReason(snapshot, matchedSkills);

        return new SearchTalentValidatedCandidate(
            CandidateId: entry.Id,
            MatchedSkills: matchedSkills,
            Reason: reason,
            CitedItems: citedItems);
    }

    private static string BuildDeterministicReason(
        SearchTalentRankingCandidate snapshot,
        IReadOnlyList<MatchedSkillBand> matchedSkills)
    {
        var clauses = new List<string>(capacity: Math.Min(matchedSkills.Count, 3));
        foreach (var skill in matchedSkills)
        {
            if (clauses.Count >= 3)
            {
                break;
            }
            var item = snapshot.Items.FirstOrDefault(i =>
                i.Skills.Any(s => string.Equals(s.Name, skill.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(s.Band, skill.Band, StringComparison.OrdinalIgnoreCase)));
            if (item is null)
            {
                continue;
            }
            var bandWord = string.Equals(skill.Band, ConfidenceBands.Strong, StringComparison.OrdinalIgnoreCase)
                ? "Strong"
                : "Developing";
            clauses.Add($"{bandWord} in {skill.Name}, shown in \"{item.Label}\".");
        }
        if (clauses.Count == 0)
        {
            return "Listed for an item with no Strong or Developing findings yet.";
        }
        return string.Join(' ', clauses);
    }

    private static bool TryDeserializePayload(string payloadJson, out Guid searchId)
    {
        searchId = Guid.Empty;
        try
        {
            var payload = JsonSerializer.Deserialize<SearchTalentPayload>(payloadJson, JsonOptions);
            if (payload is null)
            {
                return false;
            }
            searchId = payload.SearchId;
            return searchId != Guid.Empty;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
