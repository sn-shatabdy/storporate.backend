using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Security;
using Storporate.SharedKernel.Security;

namespace Storporate.Tests.Unit.Security;

/// <summary>
/// TDD coverage for <c>GoogleIdTokenValidator</c>'s pre-flight checks (empty token,
/// unconfigured ClientId). The full audience-and-signature check requires a real Google ID
/// token signed by Google's JWKs — that round-trip is deferred to Cross-Validation
/// (Phase 5) because it needs a real Google Cloud OAuth consent screen + client ID, neither
/// of which exists yet (see the Phase 3 plan's explicit "Constraint: no Google OAuth app
/// exists yet" note).
/// </summary>
public class GoogleIdTokenValidatorTests
{
    [Fact]
    public async Task ValidateAsync_EmptyToken_ThrowsGoogleAuthenticationException()
    {
        var validator = CreateValidator(clientId: "configured-client-id");

        await Assert.ThrowsAsync<GoogleAuthenticationException>(
            () => validator.ValidateAsync(string.Empty, CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_WhitespaceToken_ThrowsGoogleAuthenticationException()
    {
        var validator = CreateValidator(clientId: "configured-client-id");

        await Assert.ThrowsAsync<GoogleAuthenticationException>(
            () => validator.ValidateAsync("   ", CancellationToken.None));
    }

    [Fact]
    public async Task ValidateAsync_UnconfiguredClientId_ThrowsGoogleAuthenticationException()
    {
        var validator = CreateValidator(clientId: string.Empty);

        await Assert.ThrowsAsync<GoogleAuthenticationException>(
            () => validator.ValidateAsync("any-token", CancellationToken.None));
    }

    private static GoogleIdTokenValidator CreateValidator(string clientId) =>
        new(Options.Create(new GoogleAuthOptions { ClientId = clientId }));
}
