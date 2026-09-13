using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.Identity;

/// <summary>Outcome of a successful <see cref="VerifyOtpHandler.ExecuteAsync"/> call.</summary>
public sealed record VerifyOtpResult(User User, bool IsNewUser, AuthTokenResult Tokens);
