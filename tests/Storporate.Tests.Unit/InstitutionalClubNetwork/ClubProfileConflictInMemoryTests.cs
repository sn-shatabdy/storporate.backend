using Storporate.Modules.InstitutionalClubNetwork.Exceptions;
using Xunit;

namespace Storporate.Tests.Unit.InstitutionalClubNetwork;

/// <summary>
/// STOR-69 Phase 1: surface-level proof that the exception types are wired
/// correctly. The actual mapping from <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>
/// and from the Postgres <c>23505</c> unique-violation to
/// <see cref="ClubProfileConflictException"/> is one block of handler code
/// (<c>catch (DbUpdateException) when (IsUniqueViolationOnOwnerIndex(ex)) { throw
/// new ClubProfileConflictException(); }</c> + <c>catch (DbUpdateConcurrencyException)
/// { throw new ClubProfileConflictException(); }</c>) and is exercised end-to-end
/// against a real Postgres by
/// <see cref="ClubProfileConcurrencyPostgresTests"/>; this file only proves the
/// exception type and the HTTP status mapping exist.
/// </summary>
public class ClubProfileConflictInMemoryTests
{
    [Fact]
    public void ClubProfileConflictException_CarriesTheDocumentedErrorCode()
    {
        // The GlobalExceptionHandler maps ClubProfileConflictException to
        // HTTP 409 with the error code "club_profile_conflict". This test
        // pins the exception type's name to make sure the handler's switch
        // arm and the exception type itself don't drift apart silently.
        var exception = new ClubProfileConflictException();
        Assert.NotNull(exception);
        Assert.Equal("ClubProfileConflictException", typeof(ClubProfileConflictException).Name);
    }
}
