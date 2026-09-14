namespace Storporate.Modules.Identity;

/// <summary>
/// <paramref name="ActorType"/> is only required when <paramref name="Email"/> has no existing
/// account yet (see <see cref="VerifyOtpHandler"/>) — a returning user's login omits it.
/// </summary>
public sealed record VerifyOtpRequest(string Email, string Code, string? ActorType);
