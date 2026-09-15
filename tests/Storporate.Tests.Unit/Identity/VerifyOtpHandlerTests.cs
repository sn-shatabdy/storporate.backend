using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Identity;
using Storporate.Modules.Identity.Exceptions;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Security;

namespace Storporate.Tests.Unit.Identity;

/// <summary>
/// TDD coverage for the OTP attempt/lock/expiry state machine — the plan's explicit focus area
/// for Phase 2's tests.
/// </summary>
public class VerifyOtpHandlerTests
{
    private const string Email = "student@example.com";
    private const string Code = "123456";
    private const string WrongCode = "000000";

    [Fact]
    public async Task ExecuteAsync_NewEmailWithCorrectCodeAndStudentActorType_CreatesVerifiedUserAndIssuesTokens()
    {
        await using var dbContext = CreateDbContext();
        SeedOtpCode(dbContext, Email, Code);
        var tokenService = new FakeJwtTokenService();

        var result = await VerifyOtpHandler.ExecuteAsync(
            Email, Code, ActorTypes.Student, userAgent: null, dbContext, tokenService, CancellationToken.None);

        Assert.True(result.IsNewUser);
        Assert.Equal(ActorTypes.Student, result.User.ActorType);
        Assert.Equal(VerificationStatuses.Verified, result.User.VerificationStatus);
        Assert.Equal("fake-access-token", result.Tokens.AccessToken);
        Assert.NotNull(await dbContext.Users.SingleOrDefaultAsync(u => u.Email == Email));

        var reloadedCode = await dbContext.OtpCodes.SingleAsync();
        Assert.NotNull(reloadedCode.ConsumedAt);
    }

    [Fact]
    public async Task ExecuteAsync_NewEmailWithOrganizationActorType_CreatesUnverifiedUser()
    {
        await using var dbContext = CreateDbContext();
        SeedOtpCode(dbContext, Email, Code);
        var tokenService = new FakeJwtTokenService();

        var result = await VerifyOtpHandler.ExecuteAsync(
            Email, Code, ActorTypes.Organization, userAgent: null, dbContext, tokenService, CancellationToken.None);

        Assert.Equal(VerificationStatuses.Unverified, result.User.VerificationStatus);
    }

    [Fact]
    public async Task ExecuteAsync_NewEmailWithoutActorType_ThrowsActorTypeRequired()
    {
        await using var dbContext = CreateDbContext();
        SeedOtpCode(dbContext, Email, Code);
        var tokenService = new FakeJwtTokenService();

        await Assert.ThrowsAsync<ActorTypeRequiredException>(() =>
            VerifyOtpHandler.ExecuteAsync(Email, Code, null, null, dbContext, tokenService, CancellationToken.None));

        Assert.Empty(await dbContext.Users.ToListAsync());
    }

    [Fact]
    public async Task ExecuteAsync_NewEmailWithUnrecognizedActorType_ThrowsActorTypeRequired()
    {
        await using var dbContext = CreateDbContext();
        SeedOtpCode(dbContext, Email, Code);
        var tokenService = new FakeJwtTokenService();

        await Assert.ThrowsAsync<ActorTypeRequiredException>(() =>
            VerifyOtpHandler.ExecuteAsync(Email, Code, "NotARealActorType", null, dbContext, tokenService, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_ExistingUser_DoesNotRequireActorType()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = Email,
            ActorType = ActorTypes.Organization,
            VerificationStatus = VerificationStatuses.Unverified,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();
        SeedOtpCode(dbContext, Email, Code);
        var tokenService = new FakeJwtTokenService();

        var result = await VerifyOtpHandler.ExecuteAsync(
            Email, Code, actorType: null, userAgent: null, dbContext, tokenService, CancellationToken.None);

        Assert.False(result.IsNewUser);
        Assert.Equal(ActorTypes.Organization, result.User.ActorType);
    }

    [Fact]
    public async Task ExecuteAsync_WrongCode_IncrementsAttemptCount_AndThrowsInvalid()
    {
        await using var dbContext = CreateDbContext();
        var otpCode = SeedOtpCode(dbContext, Email, Code);
        var tokenService = new FakeJwtTokenService();

        await Assert.ThrowsAsync<OtpInvalidException>(() =>
            VerifyOtpHandler.ExecuteAsync(Email, WrongCode, ActorTypes.Student, null, dbContext, tokenService, CancellationToken.None));

        var reloaded = await dbContext.OtpCodes.SingleAsync(o => o.Id == otpCode.Id);
        Assert.Equal(1, reloaded.AttemptCount);
        Assert.Null(reloaded.ConsumedAt);
    }

    [Fact]
    public async Task ExecuteAsync_SixthAttemptAfterFiveWrongGuesses_ThrowsLocked_EvenWithCorrectCode()
    {
        await using var dbContext = CreateDbContext();
        SeedOtpCode(dbContext, Email, Code, maxAttempts: 5);
        var tokenService = new FakeJwtTokenService();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsAsync<OtpInvalidException>(() =>
                VerifyOtpHandler.ExecuteAsync(Email, WrongCode, ActorTypes.Student, null, dbContext, tokenService, CancellationToken.None));
        }

        // The 6th attempt — presenting the CORRECT code — must still fail as locked, not succeed.
        await Assert.ThrowsAsync<OtpLockedException>(() =>
            VerifyOtpHandler.ExecuteAsync(Email, Code, ActorTypes.Student, null, dbContext, tokenService, CancellationToken.None));

        Assert.Empty(await dbContext.Users.ToListAsync());
    }

    [Fact]
    public async Task ExecuteAsync_ExpiredCode_ThrowsInvalid_EvenWithCorrectCode()
    {
        await using var dbContext = CreateDbContext();
        SeedOtpCode(dbContext, Email, Code, expiresAt: DateTime.UtcNow.AddMinutes(-1));
        var tokenService = new FakeJwtTokenService();

        await Assert.ThrowsAsync<OtpInvalidException>(() =>
            VerifyOtpHandler.ExecuteAsync(Email, Code, ActorTypes.Student, null, dbContext, tokenService, CancellationToken.None));

        Assert.Empty(await dbContext.Users.ToListAsync());
    }

    [Fact]
    public async Task ExecuteAsync_NoPendingCodeForEmail_ThrowsInvalid()
    {
        await using var dbContext = CreateDbContext();
        var tokenService = new FakeJwtTokenService();

        await Assert.ThrowsAsync<OtpInvalidException>(() =>
            VerifyOtpHandler.ExecuteAsync(Email, Code, ActorTypes.Student, null, dbContext, tokenService, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyConsumedCode_CannotBeReplayed()
    {
        await using var dbContext = CreateDbContext();
        SeedOtpCode(dbContext, Email, Code);
        var tokenService = new FakeJwtTokenService();

        await VerifyOtpHandler.ExecuteAsync(Email, Code, ActorTypes.Student, null, dbContext, tokenService, CancellationToken.None);

        await Assert.ThrowsAsync<OtpInvalidException>(() =>
            VerifyOtpHandler.ExecuteAsync(Email, Code, ActorTypes.Student, null, dbContext, tokenService, CancellationToken.None));
    }

    [Fact]
    public void ValidActorTypes_DoesNotIncludeAdministrator()
    {
        // Anti-privilege-escalation invariant: a brand-new user must not be able to register
        // as ActorTypes.Administrator via /api/auth/otp/verify. Administrators bypass
        // workspace isolation by design (see the STOR-62 plan), so an attack path that
        // minted an Administrator JWT would silently grant cross-account visibility. The
        // allowlist is the only line of defense; this test pins it as a structural
        // invariant so a future refactor that drops the filter is caught at build time
        // rather than after a security incident.
        Assert.DoesNotContain(ActorTypes.Administrator, VerifyOtpHandler.ValidActorTypes);
    }

    private static WriteDbContext CreateDbContext() =>
        // STOR-62 Phase 4: see LogoutHandlerTests comment.
        new(new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options,
            new AmbientAccountContext());

    private static OtpCode SeedOtpCode(
        WriteDbContext dbContext,
        string email,
        string code,
        int maxAttempts = 5,
        DateTime? expiresAt = null)
    {
        var otpCode = new OtpCode
        {
            Id = Guid.NewGuid(),
            Email = email,
            HashedCode = Sha256CodeHasher.Hash(code),
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddMinutes(10),
            AttemptCount = 0,
            MaxAttempts = maxAttempts,
            CreatedAt = DateTime.UtcNow,
        };

        dbContext.OtpCodes.Add(otpCode);
        dbContext.SaveChanges();
        return otpCode;
    }

    private sealed class FakeJwtTokenService : IJwtTokenService
    {
        public Task<AuthTokenResult> IssueTokensAsync(User user, string? userAgent, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AuthTokenResult(
                "fake-access-token", DateTime.UtcNow.AddMinutes(15), "fake-refresh-token", DateTime.UtcNow.AddDays(7)));

        public Task<AuthTokenResult?> IssueRotatedTokensAsync(
            User user,
            string? userAgent,
            Guid familyId,
            Guid replacedSessionId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<AuthTokenResult?>(new AuthTokenResult(
                "fake-access-token-rotated", DateTime.UtcNow.AddMinutes(15), "fake-refresh-token-rotated", DateTime.UtcNow.AddDays(7)));
    }
}
