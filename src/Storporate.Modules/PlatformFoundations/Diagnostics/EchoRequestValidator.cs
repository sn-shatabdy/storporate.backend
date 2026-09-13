using FluentValidation;

namespace Storporate.Modules.PlatformFoundations.Diagnostics;

public sealed class EchoRequestValidator : AbstractValidator<EchoRequest>
{
    public EchoRequestValidator()
    {
        RuleFor(request => request.Message)
            .NotEmpty()
            .WithErrorCode("message_required")
            .WithMessage("Message must not be empty.");
    }
}
