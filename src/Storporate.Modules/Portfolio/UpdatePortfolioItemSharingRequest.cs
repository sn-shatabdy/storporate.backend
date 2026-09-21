namespace Storporate.Modules.Portfolio;

/// <summary>
/// Body for <c>PUT /api/portfolio/items/{id:guid}/sharing</c> (STOR-44 Phase 1).
/// The shape is intentionally minimal — a single boolean toggle the student uses to
/// opt this one item into employer drill-down. The endpoint reads the value verbatim
/// into <see cref="Storporate.SharedKernel.Entities.PortfolioItem.ShareOriginalWithEmployers"/>
/// and routes through <see cref="Storporate.SharedKernel.Abstractions.ITalentIndexRepository.ClearOriginalAsync"/>
/// when the new value is <see langword="false"/> (so a stale descriptor cannot outlive
/// the student's decision).
/// </summary>
/// <remarks>
/// The validator rejects a missing <see cref="ShareOriginal"/> as
/// <c>share_original_required</c> rather than defaulting to <c>false</c> — a student
/// who PUTs an empty body is almost certainly trying to remove sharing from one item and
/// mistakenly typed <c>{}</c> instead of <c>{"shareOriginal": false}</c>; failing loudly
/// matches the existing Portfolio-module pattern of stable per-field error codes.
/// </remarks>
public sealed class UpdatePortfolioItemSharingRequest
{
    /// <summary>The new value for
    /// <see cref="Storporate.SharedKernel.Entities.PortfolioItem.ShareOriginalWithEmployers"/>.
    /// Required: the validator rejects a missing value with the stable error code
    /// <c>share_original_required</c>.</summary>
    public bool? ShareOriginal { get; init; }
}
