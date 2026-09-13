using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.Identity;

/// <summary>
/// Thin wrapper around <see cref="IJwtTokenService"/> shared by every login/registration path
/// (email-OTP now via <see cref="VerifyOtpHandler"/>; Google sign-in in Phase 3) so token
/// issuance lives in exactly one place regardless of how the caller authenticated.
/// </summary>
public static class IssueSessionHandler
{
    public static Task<AuthTokenResult> ExecuteAsync(
        User user,
        string? userAgent,
        IJwtTokenService tokenService,
        CancellationToken cancellationToken) =>
        tokenService.IssueTokensAsync(user, userAgent, cancellationToken);
}
