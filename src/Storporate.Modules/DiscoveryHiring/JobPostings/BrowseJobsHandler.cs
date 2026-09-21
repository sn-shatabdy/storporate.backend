using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.DiscoveryHiring.Exceptions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.DiscoveryHiring.JobPostings;

/// <summary>
/// Student-side browse of Open job postings with a plain-language fit against the student's
/// analyzed skills (STOR-66). The student's skills are the caller's own Strong / Developing
/// findings on Analyzed portfolio items, read through the normal account-scoped queries.
/// The output never carries a score, number or percentage.
/// </summary>
public static class BrowseJobsHandler
{
    public const int MaxFitCandidates = ManageJobPostingsHandler.MaxFitCandidates;

    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 50;

    public const string StrongMatch = "Strong match";
    public const string GoodMatch = "Good match";
    public const string EarlyMatch = "Early match";
    public const string NotYet = "Not yet";

    /// <summary>Sort keys accepted by the browse endpoint.</summary>
    public static class SortKeys
    {
        public const string Fit = "fit";
        public const string Newest = "newest";
    }

    public static async Task<JobFitListResponse> ListAsync(
        string? kind,
        string? workMode,
        string? query,
        string? sort,
        int? page,
        int? pageSize,
        Guid accountId,
        WriteDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var normalizedSort = string.IsNullOrWhiteSpace(sort) ? SortKeys.Fit : sort.Trim();
        if (normalizedSort != SortKeys.Fit && normalizedSort != SortKeys.Newest)
        {
            throw new UnknownSortKeyException(
                normalizedSort,
                new[] { SortKeys.Fit, SortKeys.Newest });
        }

        var pageNumber = page ?? 1;
        if (pageNumber < 1)
        {
            throw new JobPostingQueryInvalidException();
        }

        var size = pageSize ?? DefaultPageSize;
        if (size < 1 || size > MaxPageSize)
        {
            throw new JobPostingQueryInvalidException();
        }

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        // Cap the candidate pool BEFORE computing fit, so the heaviest cost (the fit
        // calculation) is bounded. We take the newest MaxFitCandidates matches after
        // applying kind / workMode / q filters, then filter out anything past its
        // deadline at read time (defect 6).
        var candidates = dbContext.JobPostings
            .AsNoTracking()
            .Where(p => p.Status == JobPostingStatuses.Open);

        if (!string.IsNullOrWhiteSpace(kind))
        {
            candidates = candidates.Where(p => p.Kind == kind);
        }

        if (!string.IsNullOrWhiteSpace(workMode))
        {
            candidates = candidates.Where(p => p.WorkMode == workMode);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var words = SplitQuery(query);
            if (words.Count == 0)
            {
                throw new JobPostingQueryInvalidException();
            }

            foreach (var word in words)
            {
                var padded = $" {word} ";
                candidates = candidates.Where(p => (" " + p.SearchText + " ").Contains(padded));
            }
        }

        var rows = await candidates
            .OrderByDescending(p => p.CreatedAt)
            .Take(MaxFitCandidates)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var skills = await LoadStudentSkillsAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var total = rows.Count(p => !ManageJobPostingsHandler.IsExpiredAt(p, today));
        var ids = rows.Select(p => p.Id).ToList();
        var applications = await dbContext.JobApplications
            .AsNoTracking()
            .Where(a => a.StudentAccountId == accountId && ids.Contains(a.JobPostingId))
            .Select(a => new { a.JobPostingId, a.Id, a.Status })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byPosting = applications.ToDictionary(a => a.JobPostingId, a => new JobFitApplicationResponse(a.Id, a.Status));

        IEnumerable<JobPosting> ordered = normalizedSort == SortKeys.Fit
            ? rows
                .Where(p => !ManageJobPostingsHandler.IsExpiredAt(p, today))
                .OrderBy(p => FitRank(BrowseJobsHandler.ComputeFit(p, skills).Label))
                .ThenByDescending(p => p.CreatedAt)
                .ThenBy(p => p.Id)
            : rows
                .Where(p => !ManageJobPostingsHandler.IsExpiredAt(p, today))
                .OrderByDescending(p => p.CreatedAt);

        var items = ordered
            .Skip((pageNumber - 1) * size)
            .Take(size)
            .Select(p => ToFitResponse(p, skills, byPosting.GetValueOrDefault(p.Id)))
            .ToList();

        return new JobFitListResponse(items, pageNumber, size, total);
    }

    public static async Task<JobFitResponse?> GetAsync(
        Guid id,
        Guid accountId,
        WriteDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var posting = await dbContext.JobPostings
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            .ConfigureAwait(false);
        if (posting is null)
        {
            return null;
        }

        var hasApplied = await dbContext.JobApplications
            .AsNoTracking()
            .AnyAsync(a => a.StudentAccountId == accountId && a.JobPostingId == id, cancellationToken)
            .ConfigureAwait(false);

        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var isExpired = ManageJobPostingsHandler.IsExpiredAt(posting, today);
        var open = posting.Status == JobPostingStatuses.Open && !isExpired;
        if (!open && !hasApplied)
        {
            return null;
        }

        var skills = await LoadStudentSkillsAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var application = await dbContext.JobApplications
            .AsNoTracking()
            .Where(a => a.StudentAccountId == accountId && a.JobPostingId == id)
            .Select(a => new JobFitApplicationResponse(a.Id, a.Status))
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        return ToFitResponse(posting, skills, application);
    }

    /// <summary>Skill name (trimmed, case-insensitive) to the student's best band.</summary>
    internal static async Task<Dictionary<string, string>> LoadStudentSkillsAsync(
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        // Account-scoped by the global query filter (and RLS): only the caller's own rows.
        var analyzedItemIds = dbContext.PortfolioItems
            .Where(i => i.AnalysisStatus == PortfolioAnalysisStatuses.Analyzed)
            .Select(i => i.Id);

        var findings = await dbContext.PortfolioSkillFindings
            .AsNoTracking()
            .Where(f => analyzedItemIds.Contains(f.PortfolioItemId)
                && (f.ConfidenceBand == ConfidenceBands.Strong || f.ConfidenceBand == ConfidenceBands.Developing))
            .Select(f => new { f.SkillName, f.ConfidenceBand })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var best = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var finding in findings)
        {
            var name = finding.SkillName.Trim();
            if (name.Length == 0)
            {
                continue;
            }

            if (!best.TryGetValue(name, out var existing) || (existing != ConfidenceBands.Strong
                    && finding.ConfidenceBand == ConfidenceBands.Strong))
            {
                best[name] = finding.ConfidenceBand;
            }
        }

        return best;
    }

    /// <summary>
    /// The one fit computation, shared by browse and by the apply-time applicant snapshot (STOR-67).
    /// </summary>
    internal static JobFitDetailResponse ComputeFit(JobPosting p, Dictionary<string, string> studentSkills)
    {
        var required = JobPostingSkills.Deserialize(p.RequiredSkillsJson);
        var matched = new List<JobFitSkillResponse>();
        var missing = new List<string>();
        foreach (var skill in required)
        {
            if (studentSkills.TryGetValue(skill.Trim(), out var band))
            {
                matched.Add(new JobFitSkillResponse(skill, band));
            }
            else
            {
                missing.Add(skill);
            }
        }

        var strong = matched.Count(m => m.Band == ConfidenceBands.Strong);
        return new JobFitDetailResponse(ComputeLabel(matched.Count, required.Count, strong), matched, missing);
    }

    private static JobFitResponse ToFitResponse(
        JobPosting p,
        Dictionary<string, string> studentSkills,
        JobFitApplicationResponse? application)
    {
        var compensation = p.ShowCompensation
            && (p.CompensationMin.HasValue || p.CompensationMax.HasValue)
            ? new JobFitCompensationResponse(p.CompensationMin, p.CompensationMax)
            : null;
        return new(
            p.Id,
            p.Title,
            p.Kind,
            p.CompanyName,
            p.Location,
            p.WorkMode,
            p.Description,
            JobPostingSkills.Deserialize(p.RequiredSkillsJson),
            p.ApplicationDeadline,
            p.Openings,
            compensation,
            p.Status,
            ManageJobPostingsHandler.IsExpiredAt(p, DateOnly.FromDateTime(DateTime.UtcNow)),
            p.CreatedAt,
            p.UpdatedAt,
            ComputeFit(p, studentSkills),
            application);
    }

    internal static string ComputeLabel(int matched, int total, int strong)
    {
        if (total > 0 && matched == total && strong * 2 >= total)
        {
            return StrongMatch;
        }

        if (total > 0 && matched * 2 >= total)
        {
            return GoodMatch;
        }

        return matched > 0 ? EarlyMatch : NotYet;
    }

    private static int FitRank(string label) => label switch
    {
        StrongMatch => 0,
        GoodMatch => 1,
        EarlyMatch => 2,
        _ => 3,
    };

    /// <summary>
    /// Split a search query into up to 8 words. Each word must be 2 to 40 characters;
    /// anything outside the bounds throws <see cref="JobPostingQueryInvalidException"/>.
    /// Words are returned lower-cased to match the SearchText column.
    /// </summary>
    internal static List<string> SplitQuery(string query)
    {
        var trimmed = query.Trim();
        if (trimmed.Length > 100)
        {
            throw new JobPostingQueryInvalidException();
        }

        var words = trimmed
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(w => w.ToLowerInvariant())
            .Where(w => w.Length >= 2 && w.Length <= 40)
            .Take(8)
            .ToList();
        if (words.Count == 0)
        {
            throw new JobPostingQueryInvalidException();
        }

        return words;
    }
}
