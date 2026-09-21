namespace Storporate.SharedKernel.Entities;

/// <summary>
/// STOR-72: a club's sponsorship request for one of its events, sent to a company through one of
/// the company's goal sets. Non-tenant (like <see cref="SponsorshipGoalSet"/>): each side reads the
/// request by its own owner account column, enforced explicitly in the handlers, and neither owner
/// id is ever exposed in a response. Names and the goal name are snapshots taken at send time so the
/// request stays readable after a profile edit or after the company deletes the goal set
/// (<see cref="GoalSetId"/> is then null).
/// </summary>
public sealed class SponsorshipRequest
{
    public Guid Id { get; set; }

    public Guid ClubOwnerAccountId { get; set; }

    public Guid CompanyOwnerAccountId { get; set; }

    public Guid ClubProfileId { get; set; }

    /// <summary>Null after the company deleted the goal set the request was sent to.</summary>
    public Guid? GoalSetId { get; set; }

    public required string ClubName { get; set; }

    public required string University { get; set; }

    public required string CompanyName { get; set; }

    public required string GoalName { get; set; }

    public required string EventTitle { get; set; }

    public DateOnly? EventDate { get; set; }

    public required string EventDescription { get; set; }

    /// <summary>What the club asks the company for.</summary>
    public required string Ask { get; set; }

    /// <summary>Requested amount in BDT, when the club named one.</summary>
    public int? AmountRequested { get; set; }

    /// <summary>What the company gets in return.</summary>
    public required string Offer { get; set; }

    /// <summary>One of <see cref="SponsorshipRequestStatuses"/>.</summary>
    public required string Status { get; set; }

    /// <summary>The company's optional note on accept or reason on decline.</summary>
    public string? DecisionNote { get; set; }

    /// <summary>Outcome recorded on completion; the hook later stories (collaborative events, club reputation) read.</summary>
    public string? OutcomeNote { get; set; }

    /// <summary>Amount actually agreed in BDT, recorded on completion.</summary>
    public int? AgreedAmount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public DateTimeOffset? ViewedAt { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public List<SponsorshipRequestMessage> Messages { get; set; } = [];
}

/// <summary>A single message inside a <see cref="SponsorshipRequest"/> thread (STOR-72).</summary>
public sealed class SponsorshipRequestMessage
{
    public Guid Id { get; set; }

    public Guid RequestId { get; set; }

    public SponsorshipRequest? Request { get; set; }

    /// <summary>One of <see cref="SponsorshipRequestSides"/>.</summary>
    public required string SenderSide { get; set; }

    public required string Body { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public static class SponsorshipRequestStatuses
{
    public const string Sent = "Sent";
    public const string Viewed = "Viewed";
    public const string InDiscussion = "InDiscussion";
    public const string Agreed = "Agreed";
    public const string Declined = "Declined";
    public const string Completed = "Completed";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Sent,
        Viewed,
        InDiscussion,
        Agreed,
        Declined,
        Completed,
    };
}

public static class SponsorshipRequestSides
{
    public const string Club = "Club";
    public const string Company = "Company";
}
