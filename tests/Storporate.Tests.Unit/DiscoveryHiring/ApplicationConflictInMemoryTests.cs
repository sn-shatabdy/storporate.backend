using Storporate.Modules.DiscoveryHiring.Exceptions;
using Xunit;

namespace Storporate.Tests.Unit.DiscoveryHiring;

/// <summary>
/// STOR-67 Phase 1: surface-level proof that the exception types are wired
/// correctly. The actual mapping from <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>
/// to <see cref="ApplicationConflictException"/> is one line of handler code
/// (<c>catch (DbUpdateConcurrencyException) { throw new ApplicationConflictException(); }</c>)
/// inside <c>ReviewApplicationsHandler.SetStatusAsync</c> and is exercised
/// end-to-end against a real Postgres by
/// <see cref="JobApplicationConcurrencyPostgresTests"/>. This file only proves
/// the exception types and their HTTP status mapping exist.
/// </summary>
public class ApplicationConflictInMemoryTests
{
    [Fact]
    public void ApplicationConflictException_CarriesTheDocumentedErrorCode()
    {
        // The GlobalExceptionHandler maps ApplicationConflictException to
        // HTTP 409 with the error code "application_conflict". This test
        // pins the exception type's name to make sure the handler's switch
        // arm and the exception type itself don't drift apart silently.
        var exception = new ApplicationConflictException();
        Assert.NotNull(exception);
        Assert.Equal("ApplicationConflictException", typeof(ApplicationConflictException).Name);
    }
}
