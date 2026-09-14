namespace Storporate.Modules.Identity;

/// <summary>Body of <c>POST /api/auth/google</c>.</summary>
/// <param name="IdToken">The Google ID token forwarded from the frontend's NextAuth Google
/// provider, after the browser-side OAuth handshake has completed.</param>
/// <param name="ActorType">Required only when the account does not yet exist; ignored for a
/// returning user (matches the email-OTP rule in <see cref="VerifyOtpRequest"/>).</param>
public sealed record GoogleLoginRequest(string IdToken, string? ActorType);
