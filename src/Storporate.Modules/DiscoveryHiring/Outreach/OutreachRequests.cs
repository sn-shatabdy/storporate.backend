namespace Storporate.Modules.DiscoveryHiring.Outreach;

public sealed class SaveShortlistRequest
{
    public Guid CandidateId { get; init; }
}

public sealed class InviteCandidateRequest
{
    public Guid CandidateId { get; init; }

    public string? OrganizationName { get; init; }

    public string? Message { get; init; }
}

public sealed class SendOutreachMessageRequest
{
    public string? Message { get; init; }
}
