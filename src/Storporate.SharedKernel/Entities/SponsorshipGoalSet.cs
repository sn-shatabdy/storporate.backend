namespace Storporate.SharedKernel.Entities;

/// <summary>
/// STOR-70: one structured set of sponsorship goals of a company (Organization account): its
/// branding or CSR objectives, the audience it wants to reach, the event kinds it is open to and
/// a rough budget range (in BDT, Bangladeshi taka; no currency column). A company can keep several
/// sets. Deliberately NOT <see cref="IAccountScoped"/> (like <see cref="ClubProfile"/>): clubs
/// read Active sets owned by other accounts. Owner scoping for the company endpoints is enforced
/// in the handlers via <see cref="OwnerAccountId"/>, which is never exposed in any response.
/// </summary>
public sealed class SponsorshipGoalSet
{
    public Guid Id { get; set; }

    public Guid OwnerAccountId { get; set; }

    public User? Owner { get; set; }

    public required string Name { get; set; }

    public required string CompanyName { get; set; }

    /// <summary>JSON array of objectives from the fixed list.</summary>
    public required string Objectives { get; set; }

    /// <summary>JSON array of trimmed, case-insensitively de-duplicated fields of study.</summary>
    public required string AudienceFieldsOfStudy { get; set; }

    /// <summary>JSON array of study years (1..6), de-duplicated and sorted.</summary>
    public required string AudienceYears { get; set; }

    /// <summary>JSON array of cities.</summary>
    public required string AudienceCities { get; set; }

    /// <summary>JSON array of universities.</summary>
    public required string AudienceUniversities { get; set; }

    /// <summary>JSON array of event kinds from the fixed list.</summary>
    public required string EventKinds { get; set; }

    /// <summary>Lower bound of the budget range in BDT, when given.</summary>
    public int? BudgetMin { get; set; }

    /// <summary>Upper bound of the budget range in BDT, when given.</summary>
    public int? BudgetMax { get; set; }

    /// <summary>Whether clubs may see the budget range.</summary>
    public bool ShowBudget { get; set; }

    public string? Notes { get; set; }

    public required string Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public static class SponsorshipGoalStatuses
{
    public const string Active = "Active";
    public const string Paused = "Paused";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Active,
        Paused,
    };
}
