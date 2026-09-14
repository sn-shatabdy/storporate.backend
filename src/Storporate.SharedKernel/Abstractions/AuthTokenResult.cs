namespace Storporate.SharedKernel.Abstractions;

/// <summary>
/// The access + refresh token pair returned by <see cref="IJwtTokenService.IssueTokensAsync"/>.
/// <see cref="RefreshToken"/> is only ever returned to the caller at issuance — it is hashed
/// (see <see cref="Security.Sha256CodeHasher"/>) before being persisted, so this is the one and
/// only time the plaintext value exists outside the caller's hands.
/// </summary>
public sealed record AuthTokenResult(
    string AccessToken,
    DateTime AccessTokenExpiresAt,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt);
