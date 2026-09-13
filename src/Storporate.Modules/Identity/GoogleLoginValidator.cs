using FluentValidation;

namespace Storporate.Modules.Identity;

/// <summary>
/// Validates request shape only — presence of the Google ID token. Whether <c>ActorType</c> is
/// required is a business rule that depends on whether the account already exists; that check
/// lives in <see cref="GoogleLoginHandler"/>, not here (same reasoning as
/// <see cref="VerifyOtpValidator"/>).
/// </summary>
public sealed class GoogleLoginValidator : AbstractValidator<GoogleLoginRequest>
{
    public GoogleLoginValidator()
    {
        RuleFor(request => request.IdToken)
            .NotEmpty()
            .WithErrorCode("id_token_required")
            .WithMessage("Google ID token must not be empty.");
    }
}
