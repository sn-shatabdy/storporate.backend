using FluentValidation;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Security;

namespace Storporate.Modules.Identity;

/// <summary>
/// Validates request shape only (email format, code length/format). Whether <c>ActorType</c> is
/// required is a business rule that depends on whether the email already has an account — that
/// check lives in <see cref="VerifyOtpHandler"/>, not here, since a FluentValidation validator has
/// no natural place for a database lookup in this codebase's conventions.
/// </summary>
public sealed class VerifyOtpValidator : AbstractValidator<VerifyOtpRequest>
{
    public VerifyOtpValidator(IOptions<OtpOptions> otpOptions)
    {
        var codeLength = otpOptions.Value.CodeLength;

        RuleFor(request => request.Email)
            .NotEmpty()
            .WithErrorCode("email_required")
            .WithMessage("Email must not be empty.")
            .EmailAddress()
            .WithErrorCode("email_invalid")
            .WithMessage("Email must be a valid email address.");

        RuleFor(request => request.Code)
            .NotEmpty()
            .WithErrorCode("code_required")
            .WithMessage("Code must not be empty.")
            .Length(codeLength)
            .WithErrorCode("code_invalid_length")
            .WithMessage($"Code must be exactly {codeLength} digits.")
            .Matches("^[0-9]+$")
            .WithErrorCode("code_invalid_format")
            .WithMessage("Code must contain only digits.");
    }
}
