using FluentValidation;

namespace Storporate.Modules.Identity;

public sealed class RequestOtpValidator : AbstractValidator<RequestOtpRequest>
{
    public RequestOtpValidator()
    {
        RuleFor(request => request.Email)
            .NotEmpty()
            .WithErrorCode("email_required")
            .WithMessage("Email must not be empty.")
            .EmailAddress()
            .WithErrorCode("email_invalid")
            .WithMessage("Email must be a valid email address.");
    }
}
