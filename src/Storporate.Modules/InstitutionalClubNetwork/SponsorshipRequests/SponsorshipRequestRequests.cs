namespace Storporate.Modules.InstitutionalClubNetwork.SponsorshipRequests;

/// <summary>Body of <c>POST /api/sponsorship/requests</c>.</summary>
public sealed class CreateSponsorshipRequestRequest
{
    public Guid? GoalId { get; init; }

    public string? EventTitle { get; init; }

    public DateOnly? EventDate { get; init; }

    public string? EventDescription { get; init; }

    public string? Ask { get; init; }

    public int? AmountRequested { get; init; }

    public string? Offer { get; init; }
}

/// <summary>Body of the message endpoints on both sides.</summary>
public sealed class SendSponsorshipMessageRequest
{
    public string? Body { get; init; }
}

/// <summary>Body of <c>POST .../received/{id}/accept</c>.</summary>
public sealed class AcceptSponsorshipRequestRequest
{
    public string? Note { get; init; }
}

/// <summary>Body of <c>POST .../received/{id}/decline</c>.</summary>
public sealed class DeclineSponsorshipRequestRequest
{
    public string? Reason { get; init; }
}

/// <summary>Body of the complete endpoints on both sides.</summary>
public sealed class CompleteSponsorshipRequestRequest
{
    public string? OutcomeNote { get; init; }

    public int? AgreedAmount { get; init; }
}
