using FluentValidation;

namespace Storporate.Modules.Portfolio;

/// <summary>
/// Validates the <see cref="UpdatePortfolioItemSharingRequest"/> shape. The single
/// rule enforces that <see cref="UpdatePortfolioItemSharingRequest.ShareOriginal"/>
/// is present — a <see cref="Nullable{Boolean}"/> never receives a default, so a
/// caller who PUTs <c>{}</c> must be told <c>share_original_required</c> rather than
/// have the handler silently apply <c>false</c> (which would clobber a previously
/// shared item).
/// </summary>
/// <remarks>
/// Error code follows the same <c>&lt;field&gt;_&lt;reason&gt;</c> + human-readable
/// message convention the other Portfolio validators use (see
/// <see cref="CreatePortfolioItemValidator"/> remarks); the GlobalExceptionHandler
/// reads <c>ErrorCode</c> for the API surface response, so a typo here would silently
/// change the wire-level error code a client sees.
/// </remarks>
public sealed class UpdatePortfolioItemSharingValidator : AbstractValidator<UpdatePortfolioItemSharingRequest>
{
    public UpdatePortfolioItemSharingValidator()
    {
        RuleFor(request => request.ShareOriginal)
            .NotNull()
            .WithErrorCode("share_original_required")
            .WithMessage("shareOriginal is required.");
    }
}
