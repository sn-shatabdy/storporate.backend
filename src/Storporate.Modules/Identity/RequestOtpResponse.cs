namespace Storporate.Modules.Identity;

/// <summary>
/// Always the exact same shape/message regardless of whether the requested email has an account
/// — see <see cref="RequestOtpHandler"/>'s no-account-enumeration doc comment.
/// </summary>
public sealed record RequestOtpResponse(string Message);
