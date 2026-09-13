using Storporate.SharedKernel.Entities;

namespace Storporate.SharedKernel.Abstractions;

/// <summary>
/// Issues the JWT access token + opaque refresh token pair for an authenticated
/// <see cref="User"/>, persisting the refresh token's hash as a new <see cref="Session"/> row
/// with a fresh <see cref="Session.FamilyId"/>. Concrete implementation
/// (<c>Storporate.Infrastructure.Security.JwtTokenService</c>) lives in
/// <c>Storporate.Infrastructure</c>; callers in modules/API code depend only on this interface.
/// </summary>
public interface IJwtTokenService
{
    /// <param name="user">The account to issue tokens for.</param>
    /// <param name="userAgent">The raw User-Agent header of the request that triggered issuance,
    /// if any — stored on the new <see cref="Session"/> for the account page's device list
    /// (Phase 4), not parsed here.</param>
    Task<AuthTokenResult> IssueTokensAsync(
        User user,
        string? userAgent,
        CancellationToken cancellationToken = default);
}
