using FluentValidation;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.DiscoveryHiring.JobApplications;

public sealed class ApplyToJobValidator : AbstractValidator<ApplyToJobRequest>
{
    public const int DisplayNameMin = 2;
    public const int DisplayNameMax = 80;

    public ApplyToJobValidator()
    {
        // A blank name counts as "not supplied"; the handler raises
        // application_display_name_required only when the student has no profile name either.
        RuleFor(r => (r.DisplayName ?? string.Empty).Trim().Length)
            .Must(length => length == 0 || length is >= DisplayNameMin and <= DisplayNameMax)
                .WithErrorCode("application_display_name_invalid")
                .WithMessage($"Name must be between {DisplayNameMin} and {DisplayNameMax} characters.");
    }
}

public sealed class ChangeApplicationStatusValidator : AbstractValidator<ChangeApplicationStatusRequest>
{
    public ChangeApplicationStatusValidator()
    {
        RuleFor(r => r.Status)
            .Must(s => s is not null && JobApplicationStatuses.EmployerSettable.Contains(s))
                .WithErrorCode("application_status_invalid")
                .WithMessage("Status must be Shortlisted or NotSelected.");
    }
}
