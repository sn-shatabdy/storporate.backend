namespace Storporate.Modules.Identity;

/// <summary>Body of a successful <c>POST /api/auth/google</c> response.</summary>
public sealed record GoogleLoginResponse(
    Guid UserId,
    string Email,
    string ActorType,
    string VerificationStatus,
    bool IsNewUser,
    string AccessToken,
    DateTime AccessTokenExpiresAt,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt);
