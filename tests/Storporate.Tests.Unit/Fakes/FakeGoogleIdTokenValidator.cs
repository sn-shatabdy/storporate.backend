using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Security;

namespace Storporate.Tests.Unit.Fakes;

/// <summary>
/// Test double for <see cref="IGoogleIdTokenValidator"/>. <see cref="SubjectId"/> and
/// <see cref="Email"/> are configurable; calling <see cref="ValidateAsync"/> with a token that
/// exactly matches <see cref="ThrowForToken"/> throws <see cref="GoogleAuthenticationException"/>
/// to exercise the handler's catch-and-rethrow path.
/// </summary>
public sealed class FakeGoogleIdTokenValidator : IGoogleIdTokenValidator
{
    public string SubjectId { get; set; } = "google-subject-123";

    public string Email { get; set; } = "googleuser@example.com";

    public bool EmailVerified { get; set; } = true;

    /// <summary>If non-null, presenting this exact token throws <see cref="GoogleAuthenticationException"/>.</summary>
    public string? ThrowForToken { get; set; }

    public int CallCount { get; private set; }

    public Task<GoogleIdentityResult> ValidateAsync(string idToken, CancellationToken cancellationToken = default)
    {
        CallCount++;

        if (ThrowForToken is not null && idToken == ThrowForToken)
        {
            throw new GoogleAuthenticationException();
        }

        return Task.FromResult(new GoogleIdentityResult(SubjectId, Email, EmailVerified));
    }
}
