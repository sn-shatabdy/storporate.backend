namespace Storporate.Modules.InstitutionalClubNetwork.Exceptions;

/// <summary>
/// Thrown when a save against <see cref="Storporate.SharedKernel.Entities.ClubProfile"/>
/// loses a concurrency race — either two concurrent first saves hitting the
/// <c>IX_ClubProfiles_OwnerAccountId</c> unique index, or an update whose
/// Postgres <c>xmin</c> no longer matches the live row. Mapped to
/// <c>409 club_profile_conflict</c> by <c>GlobalExceptionHandler</c>.
/// </summary>
public sealed class ClubProfileConflictException : Exception
{
    public ClubProfileConflictException()
        : base("Someone else changed this club profile. Reload and try again.")
    {
    }
}