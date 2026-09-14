namespace Storporate.SharedKernel.Entities;

/// <summary>
/// A platform account. Passwordless — created on the first successful email-OTP verification or
/// the first successful Google sign-in for a given email (see the plan's Phase 2/3
/// <c>VerifyOtpHandler</c>/<c>GoogleLoginHandler</c>). See <see cref="ActorTypes"/> and
/// <see cref="VerificationStatuses"/> for the allowed values of <see cref="ActorType"/> and
/// <see cref="VerificationStatus"/>.
/// </summary>
public sealed class User
{
    public Guid Id { get; set; }

    /// <summary>Unique across all accounts; the account's login identity for both the email-OTP
    /// and Google sign-in paths.</summary>
    public required string Email { get; set; }

    /// <summary>One of <see cref="ActorTypes"/>.</summary>
    public required string ActorType { get; set; }

    /// <summary>Google's stable per-account subject identifier (the ID token's "sub" claim), set
    /// the first time this account signs in with Google. Null for an account created purely via
    /// email-OTP that has never linked Google.</summary>
    public string? GoogleSubjectId { get; set; }

    /// <summary>One of <see cref="VerificationStatuses"/>.</summary>
    public string VerificationStatus { get; set; } = VerificationStatuses.Unverified;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
