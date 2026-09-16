namespace Storporate.SharedKernel.Entities;

/// <summary>
/// The allowed values for <see cref="PortfolioItem.SubmissionType"/>: a File-backed
/// submission (the artifact is in <c>IArtifactStore</c> under <see cref="PortfolioItem.StorageKey"/>)
/// or a Link-backed submission (the artifact lives at <see cref="PortfolioItem.ExternalUrl"/>).
/// Kept as string constants (not an enum) so the database column stays a readable string.
/// </summary>
public static class PortfolioSubmissionTypes
{
    public const string File = "File";
    public const string Link = "Link";

    /// <summary>The full set of allowed submission types, exposed so the create-handler can
    /// branch off <see cref="PortfolioItem.SubmissionType"/> with a single
    /// <c>PortfolioSubmissionTypes.File</c>/<c>Link</c> comparison.</summary>
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        File,
        Link,
    };
}