namespace Storporate.Modules.Identity;

/// <summary>Body of a successful <c>GET /api/auth/sessions</c> response — one entry per
/// non-revoked, non-expired <see cref="Storporate.SharedKernel.Entities.Session"/> for the
/// caller. Excludes the hashed refresh token by design; <c>UserAgent</c> is the raw header
/// (parsed into a device/browser label client-side in Phase 4).</summary>
public sealed record ListSessionsResponse(IReadOnlyList<ListSessionsSessionItem> Sessions);

public sealed record ListSessionsSessionItem(
    Guid SessionId,
    DateTime CreatedAt,
    string? UserAgent,
    bool IsCurrent);
