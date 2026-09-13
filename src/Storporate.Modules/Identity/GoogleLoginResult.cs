using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.Identity;

/// <summary>Outcome of a successful <see cref="GoogleLoginHandler.ExecuteAsync"/> call.</summary>
public sealed record GoogleLoginResult(User User, bool IsNewUser, AuthTokenResult Tokens);
