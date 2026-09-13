namespace Storporate.SharedKernel.Abstractions;

/// <summary>
/// Provider-agnostic outbound transactional email. Concrete providers live in
/// <c>Storporate.Infrastructure</c> (Resend today); callers in modules/API code depend only on
/// this interface, mirroring the <see cref="ILlmClient"/>/<see cref="Storage.IArtifactStore"/>
/// shape used for every other external integration in this codebase.
/// </summary>
public interface IEmailSender
{
    /// <summary>Sends a one-time login/registration code to <paramref name="toEmail"/>.</summary>
    Task SendOtpCodeAsync(string toEmail, string code, CancellationToken cancellationToken = default);
}
