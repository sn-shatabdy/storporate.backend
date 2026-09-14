namespace Storporate.SharedKernel.Entities;

/// <summary>
/// A one-time email verification code, hashed at rest via
/// <see cref="Security.Sha256CodeHasher"/> — the plaintext code is never persisted. Not tied to
/// an existing <see cref="User"/> row: the same OTP flow both logs in an existing account and
/// creates a new one on first successful verification (see the plan's Phase 2
/// <c>RequestOtpHandler</c>/<c>VerifyOtpHandler</c>).
/// </summary>
public sealed class OtpCode
{
    public Guid Id { get; set; }

    public required string Email { get; set; }

    /// <summary>SHA-256 hash of the plaintext code (see <see cref="Security.Sha256CodeHasher"/>).
    /// </summary>
    public required string HashedCode { get; set; }

    public DateTime ExpiresAt { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>Once <see cref="AttemptCount"/> reaches this value, the code is locked even if it
    /// hasn't expired yet (see the plan's <c>OtpLockedException</c>).</summary>
    public int MaxAttempts { get; set; }

    /// <summary>Set once the code has been successfully verified, so it cannot be replayed even
    /// while still unexpired.</summary>
    public DateTime? ConsumedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}
