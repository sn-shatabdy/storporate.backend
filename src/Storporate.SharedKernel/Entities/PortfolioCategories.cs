namespace Storporate.SharedKernel.Entities;

/// <summary>
/// The allowed values for <see cref="PortfolioItem.Category"/>. Kept as string constants (not
/// an enum), mirroring <see cref="JobStatus"/>, <see cref="VerificationStatuses"/>, and
/// <see cref="ActorTypes"/> so the database column stays a readable string and adding a new
/// category later doesn't require a migration to widen a numeric range.
/// </summary>
public static class PortfolioCategories
{
    public const string Document = "Document";
    public const string PortfolioLink = "PortfolioLink";
    public const string Dataset = "Dataset";
    public const string ResearchPaper = "ResearchPaper";
    public const string BusinessPlan = "BusinessPlan";
    public const string DesignFile = "DesignFile";
    public const string Video = "Video";
    public const string Certificate = "Certificate";
    public const string Project = "Project";
    public const string Other = "Other";

    /// <summary>The full set of allowed categories, exposed so the create-validator can use
    /// <c>PortfolioCategories.All.Contains(category)</c> in a single check rather than
    /// enumerating ten rules.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Document,
        PortfolioLink,
        Dataset,
        ResearchPaper,
        BusinessPlan,
        DesignFile,
        Video,
        Certificate,
        Project,
        Other,
    };
}