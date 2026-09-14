using System.Security.Cryptography;
using Microsoft.Extensions.Options;
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
public static class RequestOtpHandler
{
    public static async Task ExecuteAsync(
        string email,
        WriteDbContext dbContext,
        IEmailSender emailSender,
        IOptions<OtpOptions> otpOptions,
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
    }

    private static string GenerateCode(int codeLength)
    {
        var exclusiveUpperBound = (int)Math.Pow(10, codeLength);
        var value = RandomNumberGenerator.GetInt32(0, exclusiveUpperBound);
        return value.ToString($"D{codeLength}");
    }

    internal static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();
}
