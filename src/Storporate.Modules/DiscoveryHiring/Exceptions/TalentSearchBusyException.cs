namespace Storporate.Modules.DiscoveryHiring.Exceptions;

/// <summary>
/// Thrown by <c>CreateTalentSearchHandler</c> when the calling
/// Organization already has a <c>TalentSearchRequest</c> with
/// <c>Status = Pending</c> — a <c>SearchTalent</c> job is queued or
/// running, so a second concurrent search would race the in-flight one.
/// Mapped to <c>409 talent_search_busy</c> by
/// <c>GlobalExceptionHandler</c>.
/// </summary>
public sealed class TalentSearchBusyException : Exception
{
    public TalentSearchBusyException(Guid accountId)
        : base("A search is already running. Wait for it to finish.")
    {
    }
}
