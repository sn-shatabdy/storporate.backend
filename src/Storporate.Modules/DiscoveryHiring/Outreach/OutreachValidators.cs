using FluentValidation;

namespace Storporate.Modules.DiscoveryHiring.Outreach;

public static class OutreachLimits
{
    public const int OrganizationNameMin = 2;
    public const int OrganizationNameMax = 150;
    public const int MessageMin = 1;
    public const int MessageMax = 2000;
    public const int PreviewLength = 120;
}

public sealed class InviteCandidateValidator : AbstractValidator<InviteCandidateRequest>
{
    public InviteCandidateValidator()
    {
        RuleFor(r => (r.OrganizationName ?? string.Empty).Trim().Length)
            .InclusiveBetween(OutreachLimits.OrganizationNameMin, OutreachLimits.OrganizationNameMax)
                .WithErrorCode("outreach_organization_name_invalid")
                .WithMessage(
                    $"Organization name must be between {OutreachLimits.OrganizationNameMin} and {OutreachLimits.OrganizationNameMax} characters.");

        RuleFor(r => (r.Message ?? string.Empty).Trim().Length)
            .InclusiveBetween(OutreachLimits.MessageMin, OutreachLimits.MessageMax)
                .WithErrorCode("outreach_message_invalid")
                .WithMessage($"Message must be between {OutreachLimits.MessageMin} and {OutreachLimits.MessageMax} characters.");
    }
}

public sealed class SendOutreachMessageValidator : AbstractValidator<SendOutreachMessageRequest>
{
    public SendOutreachMessageValidator()
    {
        RuleFor(r => (r.Message ?? string.Empty).Trim().Length)
            .InclusiveBetween(OutreachLimits.MessageMin, OutreachLimits.MessageMax)
                .WithErrorCode("outreach_message_invalid")
                .WithMessage($"Message must be between {OutreachLimits.MessageMin} and {OutreachLimits.MessageMax} characters.");
    }
}
