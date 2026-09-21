using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
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
    public const int MaxResults = 50;

    public const string StrongMatch = "Strong match";
    public const string GoodMatch = "Good match";
    public const string EarlyMatch = "Early match";
    public const string NotYet = "Not yet";

    public static async Task<JobFitListResponse> ListAsync(
        string? kind,
        string? workMode,
        string? query,
        Guid accountId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var postings = dbContext.JobPostings
            .AsNoTracking()
            .Where(p => p.Status == JobPostingStatuses.Open);

        if (!string.IsNullOrWhiteSpace(kind))
        {
            postings = postings.Where(p => p.Kind == kind);
        }

        if (!string.IsNullOrWhiteSpace(workMode))
        {
            postings = postings.Where(p => p.WorkMode == workMode);
        }

        var q = query?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(q))
        {
            postings = postings.Where(p =>
                p.Title.ToLower().Contains(q)
                || p.CompanyName.ToLower().Contains(q)
                || p.RequiredSkillsJson.ToLower().Contains(q));
        }

        var rows = await postings
            .OrderByDescending(p => p.CreatedAt)
            .Take(MaxResults)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var skills = await LoadStudentSkillsAsync(dbContext, cancellationToken).ConfigureAwait(false);
        var ids = rows.Select(p => p.Id).ToList();
        var applications = await dbContext.JobApplications
            .AsNoTracking()
            .Where(a => a.StudentAccountId == accountId && ids.Contains(a.JobPostingId))
            .Select(a => new { a.JobPostingId, a.Id, a.Status })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var byPosting = applications.ToDictionary(a => a.JobPostingId, a => new JobFitApplicationResponse(a.Id, a.Status));
        return new JobFitListResponse(rows
            .Select(p => ToFitResponse(p, skills, byPosting.GetValueOrDefault(p.Id)))
            .ToList());
    }

    public static async Task<JobFitResponse?> GetAsync(
        Guid id,
        Guid accountId,
        WriteDbContext dbContext,
        CancellationToken cancellationToken)
    {
        var posting = await dbContext.JobPostings
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.Status == JobPostingStatuses.Open, cancellationToken)
            .ConfigureAwait(false);
        if (posting is null)
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
        JobFitApplicationResponse? application) => new(
            p.Id, p.Title, p.Kind, p.CompanyName, p.Location, p.WorkMode, p.Description,
            JobPostingSkills.Deserialize(p.RequiredSkillsJson), p.Status, p.CreatedAt, p.UpdatedAt,
            ComputeFit(p, studentSkills), application);

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
}
