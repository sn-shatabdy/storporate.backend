using System.ComponentModel.DataAnnotations;

namespace Storporate.Infrastructure.Security;

/// <summary>
/// Configuration for <c>GoogleIdTokenValidator</c>, bound to the "GoogleAuth" section (env vars
/// <c>GoogleAuth__ClientId</c>/...). <see cref="ClientId"/> is Google's OAuth web-application
/// client ID — the same value configured in the Google sign-in flow on the frontend, and the
/// expected <c>aud</c> claim on every Google ID token we accept. See the plan's Phase 3
/// <c>GoogleIdTokenValidator</c> for the audience-check requirement.
/// </summary>
public sealed class GoogleAuthOptions
{
    public const string SectionName = "GoogleAuth";

    /// <summary>Google Cloud OAuth client ID. Required — the validator rejects every token whose
    /// <c>aud</c> claim does not match this value.</summary>
    [Required]
    public string ClientId { get; init; } = string.Empty;
}
