using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Jobs;
using Storporate.Infrastructure.Llm;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Persistence.Configurations;
using Storporate.SharedKernel;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.DiscoveryHiring.TalentIndex;

/// <summary>
/// Processes a single <see cref="Job"/> of type
/// <see cref="TalentIndexJobTypes.RefreshEntry"/>: rebuild one
/// <see cref="TalentIndexEntry"/> projection for the student identified in the
/// payload. The processor reads the student's
/// <see cref="StudentSearchProfile"/>, the analyzed <see cref="PortfolioItem"/>s
/// plus their <see cref="PortfolioSkillFinding"/>s (Strong + Developing bands only),
/// composes the non-sensitive projection fields (respecting the per-field Show
/// flags), embeds the resulting search text via
/// <see cref="IEmbeddingClient"/>, and writes the row + vector through
/// <see cref="ITalentIndexRepository"/>. When no qualifying items remain the
/// entry is deleted instead.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two-scope bracket.</b> Claim runs under
/// <see cref="IBackgroundAccountScope.BeginSystemScope"/>; the post-claim
/// per-account work runs under <c>BeginAccountScope(job.AccountId)</c>. Same
/// pattern as every other processor in the codebase, and the reason this
/// processor reads <see cref="IAccountScoped"/> tables (portfolio items,
/// findings, the search profile) without tripping the global query filter.
/// </para>
/// <para>
/// <b>Content-hash skip.</b> Before any embedding call the processor computes
/// a deterministic SHA-256 hash over the projection inputs (profile fields,
/// visible-item set, visible-skill set, items-json payload). When the hash
/// matches the stored <see cref="TalentIndexEntry.ContentHash"/> the
/// embedding step is skipped entirely — the on-disk vector is already
/// current. Re-running on a no-op refresh is the common case for an item
/// being deleted from a portfolio that no longer has any qualifying items;
/// paying the LLM cost again would be waste.
/// </para>
/// <para>
/// <b>Delete-when-empty.</b> When the student has no items with Strong or
/// Developing findings (only Missing, or none analyzed), the processor calls
/// <see cref="ITalentIndexRepository.DeleteAsync"/> instead of an upsert. The
/// Phase 2 search endpoint then sees zero rows for this student without
/// needing a separate "is this entry stale?" check. A student who later
/// re-enables a deleted item gets a fresh entry on the next refresh.
/// </para>
/// <para>
/// <b>Why the in-memory branch keeps an in-memory embedding store.</b>
/// <see cref="ITalentIndexRepository"/> already branches on
/// <c>Database.ProviderName</c>; this processor never touches the vector
/// itself, only forwards the float array the embedding client returned. The
/// test suite therefore exercises the processor end-to-end without spinning
/// up a real embedding server.
/// </para>
/// </remarks>
public sealed class RefreshTalentIndexEntryProcessor : IBackgroundJobProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Max number of characters copied into
    /// <see cref="TalentIndexEntry.SearchText"/>. Matches
    /// <see cref="TalentIndexConstants.SearchTextMaxCharacters"/> (8 000);
    /// the actual cap is applied at the end of <see cref="BuildSearchText"/>
    /// so the cap is enforced regardless of which input field overflowed.</summary>
    private const int SearchTextMaxCharacters = TalentIndexConstants.SearchTextMaxCharacters;

    private readonly WriteDbContext _dbContext;
    private readonly ITalentIndexRepository _repository;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly ILogger<RefreshTalentIndexEntryProcessor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IBackgroundAccountScope _accountScope;

    public RefreshTalentIndexEntryProcessor(
        WriteDbContext dbContext,
        ITalentIndexRepository repository,
        IEmbeddingClient embeddingClient,
        ILogger<RefreshTalentIndexEntryProcessor> logger,
        TimeProvider timeProvider,
        IBackgroundAccountScope accountScope)
    {
        _dbContext = dbContext;
        _repository = repository;
        _embeddingClient = embeddingClient;
        _logger = logger;
        _timeProvider = timeProvider;
        _accountScope = accountScope;
    }

    /// <inheritdoc />
    public string JobType => TalentIndexJobTypes.RefreshEntry;

    /// <inheritdoc />
    /// <remarks>
    /// No parent entity to mirror — the entry is either already absent
    /// (because the opt-out path already deleted it) or, if it still exists,
    /// the next refresh job will reconcile it. The reaper's Failed
    /// disposition is the strongest signal that the embedding provider is
    /// unavailable; the next manual refresh from the FE will retry once the
    /// provider comes back. No work for us here.
    /// </remarks>
    public Task OnJobAbandonedAsync(Job job, CancellationToken cancellationToken) =>
        Task.CompletedTask;

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
                TalentIndexJobTypes.RefreshEntry,
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
                _logger.LogError(ex, "Unexpected error processing job {JobId}; treating as retryable.", job.Id);
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

    private async Task ProcessClaimedJobAsync(
        Job job,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        if (!TryDeserializePayload(job.PayloadJson, out var studentAccountId))
        {
            await JobBookkeeper.MarkFailedAsync(
                _dbContext, job, nowUtc,
                "Job payload was not a valid RefreshTalentIndex payload.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var profile = await _dbContext.StudentSearchProfiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.AccountId == studentAccountId, cancellationToken)
            .ConfigureAwait(false);

        // Opt-out between enqueue and claim is the most common reason the
        // profile is missing or IsSearchable is false. The handler that
        // flipped the toggle to false already deleted the entry in the same
        // request; we just have to leave it deleted and let the job end.
        if (profile is null || !profile.IsSearchable)
        {
            await DeleteEntryAndCompleteAsync(job, studentAccountId, nowUtc, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var items = await LoadQualifyingItemsAsync(studentAccountId, cancellationToken)
            .ConfigureAwait(false);

        // No qualifying items: the student opted in but has no analyzed
        // items with Strong/Developing findings. The previous entry (if any)
        // is no longer meaningful — delete it so the Phase 2 search sees
        // zero rows for this student.
        if (items.Count == 0)
        {
            await DeleteEntryAndCompleteAsync(job, studentAccountId, nowUtc, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        var projection = BuildProjection(profile, items);
        var contentHash = ComputeContentHash(profile, items);

        // Skip the embedding call when the projection hasn't changed since
        // the last successful refresh. The vector on disk is current;
        // re-running would burn LLM budget for a zero-diff update.
        var existingEntry = await LoadExistingEntryHashAsync(studentAccountId, cancellationToken)
            .ConfigureAwait(false);
        if (existingEntry is not null
            && string.Equals(existingEntry.ContentHash, contentHash, StringComparison.Ordinal)
            && string.Equals(existingEntry.SearchText, projection.SearchText, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "Job {JobId} for student {StudentAccountId}: content hash unchanged; skipping embedding.",
                job.Id, studentAccountId);
            await JobBookkeeper.MarkSucceededAsync(_dbContext, job, nowUtc, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        float[] embedding;
        try
        {
            var vectors = await _embeddingClient
                .EmbedAsync(new[] { projection.SearchText }, cancellationToken)
                .ConfigureAwait(false);
            embedding = vectors[0];
        }
        catch (LlmProviderException ex)
        {
            _logger.LogInformation(
                "Refresh {JobId}: embedding provider failed ({Reason}).",
                job.Id, ex.Message);
            // On exhaustion the entry stays at its prior state (or absent).
            // The student can re-trigger by editing and saving their profile,
            // which re-enqueues a refresh job.
            await JobBookkeeper.RequeueOrFailAsync(
                _dbContext, job, nowUtc, "Embedding provider error.",
                _logger, cancellationToken).ConfigureAwait(false);
            return;
        }

        var updatedAt = _timeProvider.GetUtcNow();
        await _repository.UpsertAsync(
            studentAccountId: studentAccountId,
            displayName: projection.DisplayName,
            headline: projection.Headline,
            university: projection.University,
            fieldOfStudy: projection.FieldOfStudy,
            studyYear: projection.StudyYear,
            itemsJson: projection.ItemsJson,
            searchText: projection.SearchText,
            contentHash: contentHash,
            embedding: embedding,
            updatedAt: updatedAt,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        await JobBookkeeper.MarkSucceededAsync(_dbContext, job, nowUtc, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Job {JobId}: refreshed talent index entry for student {StudentAccountId} with {ItemCount} qualifying item(s).",
            job.Id, studentAccountId, items.Count);
    }

    /// <summary>
    /// Delete the entry (no-op when absent) and move the job to
    /// <see cref="JobStatus.Succeeded"/>. Centralized so the three
    /// opt-out / no-items / profile-missing branches share one path.
    /// </summary>
    private async Task DeleteEntryAndCompleteAsync(
        Job job,
        Guid studentAccountId,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        await _repository.DeleteAsync(studentAccountId, cancellationToken).ConfigureAwait(false);
        await JobBookkeeper.MarkSucceededAsync(_dbContext, job, nowUtc, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Job {JobId}: deleted talent index entry for student {StudentAccountId} (no qualifying items or opted out).",
            job.Id, studentAccountId);
    }

    /// <summary>
    /// Load the analyzed portfolio items for the student along with their
    /// Strong + Developing findings. Items with only Missing-band findings
    /// are excluded at the SQL level (saves an in-process filter); items
    /// whose <see cref="PortfolioItem.AnalysisStatus"/> is anything other
    /// than <see cref="PortfolioAnalysisStatuses.Analyzed"/> are also
    /// excluded so the projection never carries a half-analyzed item.
    /// </summary>
    private async Task<IReadOnlyList<QualifyingItem>> LoadQualifyingItemsAsync(
        Guid studentAccountId,
        CancellationToken cancellationToken)
    {
        var items = await _dbContext.PortfolioItems
            .AsNoTracking()
            .Where(item => item.AccountId == studentAccountId
                && item.AnalysisStatus == PortfolioAnalysisStatuses.Analyzed)
            .OrderBy(item => item.CreatedAt)
            .Select(item => new
            {
                item.Id,
                item.Label,
                item.Category,
                item.CustomCategoryText,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (items.Count == 0)
        {
            return Array.Empty<QualifyingItem>();
        }

        var itemIds = items.Select(i => i.Id).ToHashSet();
        var findings = await _dbContext.PortfolioSkillFindings
            .AsNoTracking()
            .Where(f => itemIds.Contains(f.PortfolioItemId)
                && (f.ConfidenceBand == ConfidenceBands.Strong
                    || f.ConfidenceBand == ConfidenceBands.Developing))
            .OrderBy(f => f.CreatedAt)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var findingsByItem = findings
            .GroupBy(f => f.PortfolioItemId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var result = new List<QualifyingItem>(items.Count);
        foreach (var item in items)
        {
            if (!findingsByItem.TryGetValue(item.Id, out var itemFindings)
                || itemFindings.Count == 0)
            {
                // Item has been analyzed but no Strong/Developing findings
                // survive — exclude it from the projection.
                continue;
            }

            result.Add(new QualifyingItem(
                Id: item.Id,
                Label: item.Label,
                CategoryDisplay: item.Category == PortfolioCategories.Other
                    ? item.CustomCategoryText ?? "Other"
                    : item.Category,
                Findings: itemFindings
                    .Select(f => new QualifyingSkill(f.SkillName, f.ConfidenceBand, f.Explanation))
                    .ToList()));
        }

        return result;
    }

    /// <summary>
    /// Read the existing entry's <see cref="TalentIndexEntry.ContentHash"/>
    /// (and its current <see cref="TalentIndexEntry.SearchText"/>) so the
    /// processor can decide whether to skip the embedding call. Reads
    /// through the global query filter indirectly — this table is NOT
    /// <see cref="IAccountScoped"/>, so we filter by StudentAccountId
    /// explicitly. Returns null when no entry exists yet.
    /// </summary>
    private async Task<ExistingEntryHash?> LoadExistingEntryHashAsync(
        Guid studentAccountId,
        CancellationToken cancellationToken)
    {
        // The vector column is invisible to EF; we only need the two text
        // columns. A two-column projection keeps the EF query plan tiny.
        var row = await _dbContext.TalentIndexEntries
            .AsNoTracking()
            .Where(e => e.StudentAccountId == studentAccountId)
            .Select(e => new { e.ContentHash, e.SearchText })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return row is null
            ? null
            : new ExistingEntryHash(row.ContentHash, row.SearchText);
    }

    /// <summary>
    /// Build the projection's visible fields from the profile (Show-flag
    /// gating) and the items (ItemsJson + SearchText composition). All
    /// self-reported fields respect the per-field Show flags so a student
    /// who hides <c>University</c> never has it copied into the
    /// projection. <see cref="TalentIndexEntry.DisplayName"/> is always
    /// populated (the validator blocks opt-in without one).
    /// </summary>
    private static Projection BuildProjection(StudentSearchProfile profile, IReadOnlyList<QualifyingItem> items)
    {
        var displayName = profile.DisplayName;
        var headline = profile.ShowHeadline ? profile.Headline : null;
        var university = profile.ShowUniversity ? profile.University : null;
        var fieldOfStudy = profile.ShowFieldOfStudy ? profile.FieldOfStudy : null;
        var studyYear = profile.ShowStudyYear ? profile.StudyYear : null;

        var itemSnapshots = items
            .Select(item => new TalentIndexItemSnapshot(
                PortfolioItemId: item.Id,
                Label: item.Label,
                Category: item.CategoryDisplay,
                Skills: item.Findings
                    .Select(f => new TalentIndexSkillSnapshot(
                        Name: f.Name,
                        Band: f.Band,
                        Reason: f.Reason))
                    .ToList()))
            .ToList();

        var itemsJson = JsonSerializer.Serialize(itemSnapshots, JsonOptions);
        var searchText = BuildSearchText(displayName, headline, university, fieldOfStudy, studyYear, itemSnapshots);

        return new Projection(
            DisplayName: displayName,
            Headline: headline,
            University: university,
            FieldOfStudy: fieldOfStudy,
            StudyYear: studyYear,
            ItemsJson: itemsJson,
            SearchText: searchText);
    }

    /// <summary>
    /// Compose the plain-text summary the embedding model runs over. Excludes
    /// item descriptions, URLs, file names, and storage keys (those are
    /// sensitive — the student owns them in <see cref="PortfolioItem"/> and
    /// RLS blocks an Organization from reading them anyway). Includes only
    /// label, category, skill name, and band — the structured fields an
    /// employer search can actually surface.
    /// </summary>
    private static string BuildSearchText(
        string displayName,
        string? headline,
        string? university,
        string? fieldOfStudy,
        int? studyYear,
        IReadOnlyList<TalentIndexItemSnapshot> itemSnapshots)
    {
        var builder = new StringBuilder(capacity: 1024);
        builder.Append("Student: ").Append(displayName).Append('\n');

        if (!string.IsNullOrWhiteSpace(headline))
        {
            builder.Append("Headline: ").Append(headline).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(university))
        {
            builder.Append("University: ").Append(university).Append('\n');
        }

        if (!string.IsNullOrWhiteSpace(fieldOfStudy))
        {
            builder.Append("Field of study: ").Append(fieldOfStudy).Append('\n');
        }

        if (studyYear.HasValue)
        {
            builder.Append("Year of study: ").Append(studyYear.Value).Append('\n');
        }

        if (itemSnapshots.Count > 0)
        {
            builder.Append("Portfolio items:\n");
            foreach (var item in itemSnapshots)
            {
                builder.Append("- ").Append(item.Label)
                    .Append(" (").Append(item.Category).Append(")\n");
                foreach (var skill in item.Skills)
                {
                    builder.Append("  * ").Append(skill.Name)
                        .Append(" [").Append(skill.Band).Append("]\n");
                }
            }
        }

        var text = builder.ToString();
        if (text.Length > SearchTextMaxCharacters)
        {
            // Truncate at the cap; the embedder still sees the most
            // important content first (profile fields + first few items).
            text = text[..SearchTextMaxCharacters];
        }
        return text;
    }

    /// <summary>
    /// Compute the deterministic SHA-256 (lowercase hex) over the projection
    /// inputs. The hash is stored on the row so a re-run can short-circuit
    /// the embedding call when nothing changed. Includes the items-json
    /// payload (so a finding-band change is detected) and the visible
    /// profile fields (so a Show-flag flip is detected).
    /// </summary>
    private static string ComputeContentHash(StudentSearchProfile profile, IReadOnlyList<QualifyingItem> items)
    {
        var builder = new StringBuilder(capacity: 512);
        builder.Append("display=").Append(profile.DisplayName).Append('|');
        builder.Append("showHeadline=").Append(profile.ShowHeadline).Append('|');
        builder.Append("headline=").Append(profile.Headline ?? string.Empty).Append('|');
        builder.Append("showUniversity=").Append(profile.ShowUniversity).Append('|');
        builder.Append("university=").Append(profile.University ?? string.Empty).Append('|');
        builder.Append("showField=").Append(profile.ShowFieldOfStudy).Append('|');
        builder.Append("field=").Append(profile.FieldOfStudy ?? string.Empty).Append('|');
        builder.Append("showYear=").Append(profile.ShowStudyYear).Append('|');
        builder.Append("year=").Append(profile.StudyYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty).Append('|');

        foreach (var item in items)
        {
            builder.Append("item:").Append(item.Id.ToString()).Append('|');
            builder.Append(item.Label).Append('|');
            builder.Append(item.CategoryDisplay).Append('|');
            foreach (var skill in item.Findings)
            {
                builder.Append("skill:").Append(skill.Name).Append('|');
                builder.Append(skill.Band).Append('|');
                builder.Append(skill.Reason).Append('|');
            }
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()), hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool TryDeserializePayload(string payloadJson, out Guid studentAccountId)
    {
        studentAccountId = Guid.Empty;
        try
        {
            var payload = JsonSerializer.Deserialize<RefreshTalentIndexPayload>(payloadJson, JsonOptions);
            if (payload is null)
            {
                return false;
            }

            studentAccountId = payload.StudentAccountId;
            return studentAccountId != Guid.Empty;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>One analyzed item whose findings are all Strong/Developing.</summary>
    private sealed record QualifyingItem(
        Guid Id,
        string Label,
        string CategoryDisplay,
        IReadOnlyList<QualifyingSkill> Findings);

    private sealed record QualifyingSkill(string Name, string Band, string Reason);

    /// <summary>Existing entry hash + search text. Used to decide whether
    /// the embedding call can be skipped on a no-op refresh.</summary>
    private sealed record ExistingEntryHash(string ContentHash, string SearchText);

    /// <summary>The visible projection built from the profile + items.</summary>
    private sealed record Projection(
        string DisplayName,
        string? Headline,
        string? University,
        string? FieldOfStudy,
        int? StudyYear,
        string ItemsJson,
        string SearchText);
}
