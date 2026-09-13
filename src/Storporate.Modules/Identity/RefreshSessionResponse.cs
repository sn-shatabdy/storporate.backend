namespace Storporate.Modules.Identity;

/// <summary>Body of a successful <c>POST /api/auth/refresh</c> response.</summary>
public sealed record RefreshSessionResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAt,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt);
