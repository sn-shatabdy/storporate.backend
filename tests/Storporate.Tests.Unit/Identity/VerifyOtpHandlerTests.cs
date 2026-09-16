using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Identity;
using Storporate.Modules.Identity.Exceptions;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Auditing;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Security;
using Storporate.Tests.Unit.Fakes;

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
        var auditLogWriter = new FakeAuditLogWriter();

        var result = await VerifyOtpHandler.ExecuteAsync(
            Email, Code, ActorTypes.Student, userAgent: null, dbContext, tokenService, auditLogWriter, CancellationToken.None);

        Assert.True(result.IsNewUser);
        Assert.Equal(ActorTypes.Student, result.User.ActorType);
        Assert.Equal(VerificationStatuses.Verified, result.User.VerificationStatus);
        Assert.Equal("fake-access-token", result.Tokens.AccessToken);
        Assert.NotNull(await dbContext.Users.SingleOrDefaultAsync(u => u.Email == Email));

        var reloadedCode = await dbContext.OtpCodes.SingleAsync();
        Assert.NotNull(reloadedCode.ConsumedAt);

        // STOR-63 Phase 2: success path audits against the resulting User.Id, not the email.
        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("login_succeeded", entry.Action);
        Assert.Equal("User", entry.ResourceType);
        Assert.Equal(result.User.Id.ToString(), entry.ResourceId);
    }

    [Fact]
    public async Task ExecuteAsync_NewEmailWithOrganizationActorType_CreatesUnverifiedUser()
    {
        await using var dbContext = CreateDbContext();
        SeedOtpCode(dbContext, Email, Code);
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        var result = await VerifyOtpHandler.ExecuteAsync(
            Email, Code, ActorTypes.Organization, userAgent: null, dbContext, tokenService, auditLogWriter, CancellationToken.None);

        Assert.Equal(VerificationStatuses.Unverified, result.User.VerificationStatus);
    }

    [Fact]
    public async Task ExecuteAsync_NewEmailWithoutActorType_ThrowsActorTypeRequired()
    {
        await using var dbContext = CreateDbContext();
        SeedOtpCode(dbContext, Email, Code);
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        var thrown = await Assert.ThrowsAsync<ActorTypeRequiredException>(() =>
            VerifyOtpHandler.ExecuteAsync(Email, Code, null, null, dbContext, tokenService, auditLogWriter, CancellationToken.None));

        Assert.Empty(await dbContext.Users.ToListAsync());

        // STOR-63 Phase 2: the actor_type-required branch must record its audit row even
        // though the handler subsequently throws — the throw is the user's view, the audit
        // row is the system's. Both exist.
        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("otp_verify_failed", entry.Action);
        Assert.Equal("User", entry.ResourceType);
        Assert.Equal(Email, entry.ResourceId);
        Assert.Equal(AuditReasons.ActorTypeMissing, entry.MetadataJson);
        Assert.NotNull(thrown);
    }

    [Fact]
    public async Task ExecuteAsync_NewEmailWithUnrecognizedActorType_ThrowsActorTypeRequired_AndWritesActorTypeUnrecognizedReason()
    {
        // Regression guard: the missing-actor-type branch and the unrecognized-actor-type
        // branch must record distinct reason strings so a future admin UI can distinguish
        // "caller sent nothing" from "caller sent a typo'd value" without re-walking
        // User rows. Earlier versions of this handler collapsed both into a single
        // "actor_type_required" reason, which lied about which branch fired.
        await using var dbContext = CreateDbContext();
        SeedOtpCode(dbContext, Email, Code);
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        await Assert.ThrowsAsync<ActorTypeRequiredException>(() =>
            VerifyOtpHandler.ExecuteAsync(Email, Code, "NotARealActorType", null, dbContext, tokenService, auditLogWriter, CancellationToken.None));

        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("otp_verify_failed", entry.Action);
        Assert.Equal(AuditReasons.ActorTypeUnrecognized, entry.MetadataJson);
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
        var auditLogWriter = new FakeAuditLogWriter();

        var result = await VerifyOtpHandler.ExecuteAsync(
            Email, Code, actorType: null, userAgent: null, dbContext, tokenService, auditLogWriter, CancellationToken.None);

        Assert.False(result.IsNewUser);
        Assert.Equal(ActorTypes.Organization, result.User.ActorType);
    }

    [Fact]
    public async Task ExecuteAsync_WrongCode_IncrementsAttemptCount_AndThrowsInvalid_AndAuditsFailure()
    {
        await using var dbContext = CreateDbContext();
        var otpCode = SeedOtpCode(dbContext, Email, Code);
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        await Assert.ThrowsAsync<OtpInvalidException>(() =>
            VerifyOtpHandler.ExecuteAsync(Email, WrongCode, ActorTypes.Student, null, dbContext, tokenService, auditLogWriter, CancellationToken.None));

        var reloaded = await dbContext.OtpCodes.SingleAsync(o => o.Id == otpCode.Id);
        Assert.Equal(1, reloaded.AttemptCount);
        Assert.Null(reloaded.ConsumedAt);

        // STOR-63 Phase 2: the wrong-code branch audits "wrong_code" specifically — not the
        // generic "invalid" action — so a future audit-log query can distinguish a presentation
        // of a typo'd code from one of an expired or already-consumed code without
        // re-walking OtpCode rows.
        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("otp_verify_failed", entry.Action);
        Assert.Equal(AuditReasons.OtpWrongCode, entry.MetadataJson);
    }

    [Fact]
    public async Task ExecuteAsync_SixthAttemptAfterFiveWrongGuesses_ThrowsLocked_EvenWithCorrectCode()
    {
        await using var dbContext = CreateDbContext();
        SeedOtpCode(dbContext, Email, Code, maxAttempts: 5);
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Assert.ThrowsAsync<OtpInvalidException>(() =>
                VerifyOtpHandler.ExecuteAsync(Email, WrongCode, ActorTypes.Student, null, dbContext, tokenService, auditLogWriter, CancellationToken.None));
        }

        // The 6th attempt — presenting the CORRECT code — must still fail as locked, not succeed.
        await Assert.ThrowsAsync<OtpLockedException>(() =>
            VerifyOtpHandler.ExecuteAsync(Email, Code, ActorTypes.Student, null, dbContext, tokenService, auditLogWriter, CancellationToken.None));

        Assert.Empty(await dbContext.Users.ToListAsync());

        // The 5 wrong-code attempts produce 5 "otp_verify_failed" rows; the 6th locked
        // attempt produces one distinct "otp_locked" row. All 6 rows exist regardless of the
        // throw that follows each call — that's the whole point of the write-before-throw
        // discipline.
        Assert.Equal(6, auditLogWriter.Recorded.Count);
        Assert.Equal(5, auditLogWriter.Recorded.Count(e => e.Action == "otp_verify_failed"));
        var lockedEntry = Assert.Single(auditLogWriter.Recorded, e => e.Action == "otp_locked");
        Assert.Equal("User", lockedEntry.ResourceType);
        Assert.Equal(Email, lockedEntry.ResourceId);
        Assert.Null(lockedEntry.MetadataJson);
    }

    [Fact]
    public async Task ExecuteAsync_ExpiredCode_ThrowsInvalid_AndAuditsFailure()
    {
        await using var dbContext = CreateDbContext();
        SeedOtpCode(dbContext, Email, Code, expiresAt: DateTime.UtcNow.AddMinutes(-1));
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        await Assert.ThrowsAsync<OtpInvalidException>(() =>
            VerifyOtpHandler.ExecuteAsync(Email, Code, ActorTypes.Student, null, dbContext, tokenService, auditLogWriter, CancellationToken.None));

        Assert.Empty(await dbContext.Users.ToListAsync());

        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("otp_verify_failed", entry.Action);
        Assert.Equal(AuditReasons.OtpExpired, entry.MetadataJson);
    }

    [Fact]
    public async Task ExecuteAsync_NoPendingCodeForEmail_ThrowsInvalid_AndAuditsFailure()
    {
        await using var dbContext = CreateDbContext();
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        await Assert.ThrowsAsync<OtpInvalidException>(() =>
            VerifyOtpHandler.ExecuteAsync(Email, Code, ActorTypes.Student, null, dbContext, tokenService, auditLogWriter, CancellationToken.None));

        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("otp_verify_failed", entry.Action);
        Assert.Equal(AuditReasons.OtpNoPendingCode, entry.MetadataJson);
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyConsumedCode_CannotBeReplayed()
    {
        await using var dbContext = CreateDbContext();
        SeedOtpCode(dbContext, Email, Code);
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        await VerifyOtpHandler.ExecuteAsync(Email, Code, ActorTypes.Student, null, dbContext, tokenService, auditLogWriter, CancellationToken.None);

        await Assert.ThrowsAsync<OtpInvalidException>(() =>
            VerifyOtpHandler.ExecuteAsync(Email, Code, ActorTypes.Student, null, dbContext, tokenService, auditLogWriter, CancellationToken.None));
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
