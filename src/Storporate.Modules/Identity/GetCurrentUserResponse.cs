namespace Storporate.Modules.Identity;

/// <summary>Body of a successful <c>GET /api/auth/me</c> response.</summary>
public sealed record GetCurrentUserResponse(
    Guid UserId,
    string Email,
    string ActorType,
    string VerificationStatus);
