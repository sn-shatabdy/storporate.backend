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

    /// <summary>
    /// Issues a new access + refresh token pair whose persisted <see cref="Session"/> belongs to
    /// the same <paramref name="familyId"/> as an existing rotation chain. The new session's
    /// <c>ReplacedBySessionId</c> link is wired back to <paramref name="replacedSessionId"/> so
    /// presenting the rotated-out token again is detectable as reuse/theft (see the plan's
    /// Phase 3 <c>RefreshSessionHandler</c>).
    /// </summary>
    Task<AuthTokenResult> IssueRotatedTokensAsync(
        User user,
        string? userAgent,
        Guid familyId,
        Guid replacedSessionId,
        CancellationToken cancellationToken = default);
}
