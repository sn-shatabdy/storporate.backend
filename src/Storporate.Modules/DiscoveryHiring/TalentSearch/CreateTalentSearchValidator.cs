using FluentValidation;

namespace Storporate.Modules.DiscoveryHiring.TalentSearch;

/// <summary>
/// Validates the <see cref="CreateTalentSearchRequest"/> body before the
/// handler runs. Mirrors <see cref="SearchableProfile.UpdateSearchableProfileValidator"/>'s
/// shape: each rule carries an explicit <c>WithErrorCode</c> +
/// <c>WithMessage</c> pair so the FE can branch on the wire-level
/// <c>errorCode</c> without parsing the message string.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two error codes only.</b> The plan requires exactly these two: a
/// short query (<c>talent_search_query_too_short</c>) and a long query
/// (<c>talent_search_query_too_long</c>). Both are checked against the
/// <em>trimmed</em> query so leading / trailing whitespace does not
/// silently fail validation (or silently pass).
/// </para>
/// </remarks>
public sealed class CreateTalentSearchValidator : AbstractValidator<CreateTalentSearchRequest>
{
    public CreateTalentSearchValidator()
    {
        RuleFor(request => request.Query!.Trim())
            .NotEmpty()
                .WithErrorCode("talent_search_query_required")
                .WithMessage("A search query is required.")
            .Must(trimmed => trimmed.Length >= TalentSearchDefaults.MinQueryLength)
                .WithErrorCode("talent_search_query_too_short")
                .WithMessage($"Search query must be at least {TalentSearchDefaults.MinQueryLength} characters after trimming.")
            .Must(trimmed => trimmed.Length <= TalentSearchDefaults.MaxQueryLength)
                .WithErrorCode("talent_search_query_too_long")
                .WithMessage($"Search query must be at most {TalentSearchDefaults.MaxQueryLength} characters after trimming.");
    }
}
