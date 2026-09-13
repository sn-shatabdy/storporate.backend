namespace Storporate.Modules.Identity;

/// <summary>Body of <c>POST /api/auth/refresh</c>.</summary>
public sealed record RefreshSessionRequest(string RefreshToken);
