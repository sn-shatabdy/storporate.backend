using FluentValidation;

namespace Storporate.Modules.Identity;

public sealed class RefreshSessionValidator : AbstractValidator<RefreshSessionRequest>
{
    public RefreshSessionValidator()
    {
        RuleFor(request => request.RefreshToken)
            .NotEmpty()
            .WithErrorCode("refresh_token_required")
            .WithMessage("Refresh token must not be empty.");
    }
}
