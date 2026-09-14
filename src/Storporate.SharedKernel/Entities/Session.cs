namespace Storporate.SharedKernel.Entities;

/// <summary>
/// A refresh-token session, hashed at rest via <see cref="Security.Sha256CodeHasher"/> — the
/// plaintext refresh token is never persisted. Sessions form rotation chains linked by
/// <see cref="FamilyId"/>: each successful refresh issues a new <see cref="Session"/> row and
/// sets <see cref="ReplacedBySessionId"/> on the session it replaces, so presenting an
/// already-replaced refresh token again is detectable as reuse/theft and revokes the whole
/// family (see the plan's Phase 3 <c>RefreshSessionHandler</c>).
/// </summary>
public sealed class Session
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public User? User { get; set; }

    /// <summary>SHA-256 hash of the opaque refresh token (see
    /// <see cref="Security.Sha256CodeHasher"/>).</summary>
    public required string HashedRefreshToken { get; set; }

    /// <summary>Groups every session in one rotation chain, starting from the session created at
    /// initial login/registration. Constant across rotations; used to revoke an entire chain at
    /// once on reuse detection or "log out of all devices."</summary>
    public Guid FamilyId { get; set; }

    /// <summary>Set when this session is rotated out by a successful refresh, pointing at the
    /// session that replaced it. Null while this session is still the live end of its chain.
    /// </summary>
    public Guid? ReplacedBySessionId { get; set; }

    public Session? ReplacedBySession { get; set; }

    public DateTime ExpiresAt { get; set; }

    /// <summary>Set when this session is explicitly revoked (logout, logout-all, or
    /// reuse/theft-detection) rather than merely rotated out by a normal refresh.</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>Raw User-Agent header captured at session creation, for the account page's
    /// device/browser display (parsed into a human-readable label client-side — Phase 4 — not
    /// here).</summary>
    public string? UserAgent { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
