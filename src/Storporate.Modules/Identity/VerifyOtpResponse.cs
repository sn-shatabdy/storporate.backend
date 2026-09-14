namespace Storporate.Modules.Identity;

public sealed record VerifyOtpResponse(
    Guid UserId,
    string Email,
    string ActorType,
    string VerificationStatus,
    bool IsNewUser,
    string AccessToken,
    DateTime AccessTokenExpiresAt,
    string RefreshToken,
    DateTime RefreshTokenExpiresAt);
