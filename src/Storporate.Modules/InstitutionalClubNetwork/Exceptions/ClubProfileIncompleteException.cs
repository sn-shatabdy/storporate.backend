namespace Storporate.Modules.InstitutionalClubNetwork.Exceptions;

/// <summary>
/// Thrown when a club tries to publish a profile that is missing required parts. The message
/// names what is missing. Mapped to <c>400 club_profile_incomplete</c> by <c>GlobalExceptionHandler</c>.
/// </summary>
public sealed class ClubProfileIncompleteException(string message) : Exception(message);
