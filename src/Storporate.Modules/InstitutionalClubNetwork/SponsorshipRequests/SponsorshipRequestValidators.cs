using FluentValidation;

namespace Storporate.Modules.InstitutionalClubNetwork.SponsorshipRequests;

/// <summary>Validates <see cref="CreateSponsorshipRequestRequest"/>. Each rule carries an error code and message.</summary>
public sealed class CreateSponsorshipRequestValidator : AbstractValidator<CreateSponsorshipRequestRequest>
{
    public const int EventTitleMin = 2;
    public const int EventTitleMax = 150;
    public const int EventDescriptionMin = 20;
    public const int EventDescriptionMax = 3000;
    public const int AskMin = 10;
    public const int AskMax = 1500;
    public const int OfferMin = 10;
    public const int OfferMax = 1500;
    public const int AmountMax = 1_000_000_000;

    public CreateSponsorshipRequestValidator(TimeProvider timeProvider)
    {
        RuleFor(r => r.GoalId)
            .Must(g => g is { } id && id != Guid.Empty)
                .WithErrorCode("sponsorship_request_goal_required")
                .WithMessage("Choose the company goal set this request is for.");

        RuleFor(r => (r.EventTitle ?? string.Empty).Trim().Length)
            .InclusiveBetween(EventTitleMin, EventTitleMax)
                .WithErrorCode("sponsorship_request_event_title_invalid")
                .WithMessage($"The event title must be between {EventTitleMin} and {EventTitleMax} characters.");

        RuleFor(r => r.EventDate)
            .Must(d => d is null || d.Value >= DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime).AddDays(-1))
                .WithErrorCode("sponsorship_request_event_date_invalid")
                .WithMessage("The event date cannot be in the past.");

        RuleFor(r => (r.EventDescription ?? string.Empty).Trim().Length)
            .InclusiveBetween(EventDescriptionMin, EventDescriptionMax)
                .WithErrorCode("sponsorship_request_event_description_invalid")
                .WithMessage($"The event description must be between {EventDescriptionMin} and {EventDescriptionMax} characters.");

        RuleFor(r => (r.Ask ?? string.Empty).Trim().Length)
            .InclusiveBetween(AskMin, AskMax)
                .WithErrorCode("sponsorship_request_ask_invalid")
                .WithMessage($"What you ask for must be between {AskMin} and {AskMax} characters.");

        RuleFor(r => r.AmountRequested)
            .Must(a => a is null or >= 0 and <= AmountMax)
                .WithErrorCode("sponsorship_request_amount_invalid")
                .WithMessage($"The amount must be between 0 and {AmountMax:N0} BDT.");

        RuleFor(r => (r.Offer ?? string.Empty).Trim().Length)
            .InclusiveBetween(OfferMin, OfferMax)
                .WithErrorCode("sponsorship_request_offer_invalid")
                .WithMessage($"What the company gets must be between {OfferMin} and {OfferMax} characters.");
    }
}

public sealed class SendSponsorshipMessageValidator : AbstractValidator<SendSponsorshipMessageRequest>
{
    public SendSponsorshipMessageValidator()
    {
        RuleFor(r => (r.Body ?? string.Empty).Trim().Length)
            .InclusiveBetween(1, SponsorshipRequestRules.MaxMessageLength)
                .WithErrorCode("sponsorship_request_message_invalid")
                .WithMessage($"A message must be between 1 and {SponsorshipRequestRules.MaxMessageLength} characters.");
    }
}

public sealed class AcceptSponsorshipRequestValidator : AbstractValidator<AcceptSponsorshipRequestRequest>
{
    public const int NoteMax = 1000;

    public AcceptSponsorshipRequestValidator()
    {
        RuleFor(r => (r.Note ?? string.Empty).Trim().Length)
            .LessThanOrEqualTo(NoteMax)
                .WithErrorCode("sponsorship_request_note_invalid")
                .WithMessage($"The note must be at most {NoteMax} characters.");
    }
}

public sealed class DeclineSponsorshipRequestValidator : AbstractValidator<DeclineSponsorshipRequestRequest>
{
    public const int ReasonMax = 1000;

    public DeclineSponsorshipRequestValidator()
    {
        RuleFor(r => (r.Reason ?? string.Empty).Trim().Length)
            .LessThanOrEqualTo(ReasonMax)
                .WithErrorCode("sponsorship_request_reason_invalid")
                .WithMessage($"The reason must be at most {ReasonMax} characters.");
    }
}

public sealed class CompleteSponsorshipRequestValidator : AbstractValidator<CompleteSponsorshipRequestRequest>
{
    public const int OutcomeNoteMax = 1000;
    public const int AgreedAmountMax = 1_000_000_000;

    public CompleteSponsorshipRequestValidator()
    {
        RuleFor(r => (r.OutcomeNote ?? string.Empty).Trim().Length)
            .InclusiveBetween(1, OutcomeNoteMax)
                .WithErrorCode("sponsorship_request_outcome_note_invalid")
                .WithMessage($"Describe the outcome in 1 to {OutcomeNoteMax} characters.");

        RuleFor(r => r.AgreedAmount)
            .Must(a => a is null or >= 0 and <= AgreedAmountMax)
                .WithErrorCode("sponsorship_request_agreed_amount_invalid")
                .WithMessage($"The agreed amount must be between 0 and {AgreedAmountMax:N0} BDT.");
    }
}
