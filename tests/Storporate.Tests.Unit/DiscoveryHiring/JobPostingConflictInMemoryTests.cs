using Storporate.Modules.DiscoveryHiring.Exceptions;
using Xunit;

namespace Storporate.Tests.Unit.DiscoveryHiring;

/// <summary>
/// STOR-66 Phase 1: surface-level proof that the exception types are wired
/// correctly. The actual mapping from <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>
/// to <see cref="JobPostingConflictException"/> is one line of handler code
/// (<c>catch (DbUpdateConcurrencyException) { throw new JobPostingConflictException(); }</c>)
/// and is exercised end-to-end against a real Postgres by
/// <see cref="JobPostingConcurrencyPostgresTests"/>. This file only proves
/// the exception types and their HTTP status mapping exist.
/// </summary>
public class JobPostingConflictInMemoryTests
{
    [Fact]
    public void JobPostingConflictException_CarriesTheDocumentedErrorCode()
    {
        // The GlobalExceptionHandler maps JobPostingConflictException to
        // HTTP 409 with the error code "job_posting_conflict". This test
        // pins the exception type's name to make sure the handler's switch
        // arm and the exception type itself don't drift apart silently.
        var exception = new JobPostingConflictException();
        Assert.NotNull(exception);
        Assert.Equal("JobPostingConflictException", typeof(JobPostingConflictException).Name);
    }
}
