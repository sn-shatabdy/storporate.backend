using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Security;
using Storporate.Modules.Identity;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Security;

namespace Storporate.Tests.Unit.Identity;

public class RequestOtpHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_StoresHashedOtpCode_AndSendsPlaintextCodeViaEmailSender()
    {
        await using var dbContext = CreateDbContext();
        var emailSender = new FakeEmailSender();
        var otpOptions = Options.Create(new OtpOptions { CodeLength = 6, ExpiryMinutes = 10, MaxAttempts = 5 });

        await RequestOtpHandler.ExecuteAsync("Test@Example.com", dbContext, emailSender, otpOptions, CancellationToken.None);

        var storedCode = await dbContext.OtpCodes.SingleAsync();

        // Normalized (lower-cased) — no-account-enumeration and case-insensitive lookup rely on
        // storing/comparing a single canonical form.
        Assert.Equal("test@example.com", storedCode.Email);
        Assert.Equal(6, emailSender.LastCode!.Length);
        Assert.Equal(Sha256CodeHasher.Hash(emailSender.LastCode!), storedCode.HashedCode);
        Assert.NotEqual(emailSender.LastCode, storedCode.HashedCode);
        Assert.Equal(5, storedCode.MaxAttempts);
        Assert.Equal(0, storedCode.AttemptCount);
        Assert.Null(storedCode.ConsumedAt);
    }

    [Fact]
    public async Task ExecuteAsync_RespectsConfiguredCodeLength()
    {
        await using var dbContext = CreateDbContext();
        var emailSender = new FakeEmailSender();
        var otpOptions = Options.Create(new OtpOptions { CodeLength = 8, ExpiryMinutes = 10, MaxAttempts = 5 });

        await RequestOtpHandler.ExecuteAsync("user@example.com", dbContext, emailSender, otpOptions, CancellationToken.None);

        Assert.Equal(8, emailSender.LastCode!.Length);
        Assert.True(long.TryParse(emailSender.LastCode, out _));
    }

    private static WriteDbContext CreateDbContext() =>
        // STOR-62 Phase 4: see LogoutHandlerTests comment.
        new(new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options,
            new AmbientAccountContext());

    private sealed class FakeEmailSender : IEmailSender
    {
        public string? LastCode { get; private set; }

        public Task SendOtpCodeAsync(string toEmail, string code, CancellationToken cancellationToken = default)
        {
            LastCode = code;
            return Task.CompletedTask;
        }
    }
}
