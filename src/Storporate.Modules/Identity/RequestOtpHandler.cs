using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Security;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Security;

namespace Storporate.Modules.Identity;

/// <summary>
/// Generates and emails a one-time login/registration code for an email address. Never queries
/// <c>Users</c> — the same OTP row is written and the same email is sent whether or not the
/// address already has an account, so <see cref="IdentityEndpoints"/> can return an identical
/// response body/status either way (no account-enumeration leak).
/// </summary>
/// <remarks>
/// After the OTP is successfully sent, the handler stamps one
/// <c>"otp_requested"</c> row into the audit log with <c>ResourceId</c> set to the target
/// (normalized) email. The write happens at the very end of the happy path so any failure
/// above this line is naturally not audited — by design, an audit-write failure (or a handler
/// exception) before this line means no row exists for the event.
/// </remarks>
public static class RequestOtpHandler
{
    public static async Task ExecuteAsync(
        string email,
        WriteDbContext dbContext,
        IEmailSender emailSender,
        IOptions<OtpOptions> otpOptions,
        IAuditLogWriter auditLogWriter,
        CancellationToken cancellationToken)
    {
        var normalizedEmail = NormalizeEmail(email);
        var options = otpOptions.Value;
        var now = DateTime.UtcNow;

        var code = GenerateCode(options.CodeLength);

        var otpCode = new OtpCode
        {
            Id = Guid.NewGuid(),
            Email = normalizedEmail,
            HashedCode = Sha256CodeHasher.Hash(code),
            ExpiresAt = now.AddMinutes(options.ExpiryMinutes),
            AttemptCount = 0,
            MaxAttempts = options.MaxAttempts,
            CreatedAt = now,
        };

        dbContext.OtpCodes.Add(otpCode);
        await dbContext.SaveChangesAsync(cancellationToken);

        await emailSender.SendOtpCodeAsync(normalizedEmail, code, cancellationToken);

        // Stamp the audit row only after the email send has been scheduled, so a failure
        // above this line is naturally un-audited (matching the "real event happened"
        // invariant — we record what actually occurred, not what was attempted).
        // The writer swallows-and-logs its own failures (see AuditLogWriter's class remarks),
        // so a misbehaving audit DB cannot break the OTP request flow here.
        await auditLogWriter.WriteAsync(
            action: "otp_requested",
            resourceType: "User",
            resourceId: normalizedEmail,
            cancellationToken: cancellationToken);
    }

    private static string GenerateCode(int codeLength)
    {
        var exclusiveUpperBound = (int)Math.Pow(10, codeLength);
        var value = RandomNumberGenerator.GetInt32(0, exclusiveUpperBound);
        return value.ToString($"D{codeLength}");
    }

    internal static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
