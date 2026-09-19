using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Security;
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
/// for Phase 2's tests. The <c>ExecuteAsync_NewEmail_WithMasterCodeBypass*</c> / <c>*_Production*</c>
/// facts cover the dev-only master-code path that lets local callers skip the inbox round-trip.
/// </summary>
public class VerifyOtpHandlerTests
{
    private const string Email = "student@example.com";
    private const string Code = "123456";
    private const string WrongCode = "000000";
    private const string MasterCode = "000000";

    /// <summary>
    /// Used by every existing test (which must continue to exercise the real OTP flow with the
    /// bypass definitively OFF) and by two of the new tests (the "Production" gating tests that
    /// prove even a configured master code cannot fire outside Development).
    /// </summary>
    private static IOptions<OtpOptions> MasterCodeDisabled() =>
        Options.Create(new OtpOptions { MasterCode = null });

    /// <summary>Used by the Development tests that exercise the bypass.</summary>
    private static IOptions<OtpOptions> MasterCodeEnabled() =>
        Options.Create(new OtpOptions { MasterCode = MasterCode });

    [Fact]
    public async Task ExecuteAsync_NewEmailWithCorrectCodeAndStudentActorType_CreatesVerifiedUserAndIssuesTokens()
    {
        await using var dbContext = CreateDbContext();
        SeedOtpCode(dbContext, Email, Code);
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        var result = await VerifyOtpHandler.ExecuteAsync(
            Email, Code, ActorTypes.Student, userAgent: null, dbContext, tokenService, auditLogWriter,
            MasterCodeDisabled(), FakeHostEnvironment.Production(), CancellationToken.None);

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
            Email, Code, ActorTypes.Organization, userAgent: null, dbContext, tokenService, auditLogWriter,
            MasterCodeDisabled(), FakeHostEnvironment.Production(), CancellationToken.None);

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
            VerifyOtpHandler.ExecuteAsync(Email, Code, null, null, dbContext, tokenService, auditLogWriter,
                MasterCodeDisabled(), FakeHostEnvironment.Production(), CancellationToken.None));

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
            VerifyOtpHandler.ExecuteAsync(Email, Code, "NotARealActorType", null, dbContext, tokenService, auditLogWriter,
                MasterCodeDisabled(), FakeHostEnvironment.Production(), CancellationToken.None));

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
            Email, Code, actorType: null, userAgent: null, dbContext, tokenService, auditLogWriter,
            MasterCodeDisabled(), FakeHostEnvironment.Production(), CancellationToken.None);

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
            VerifyOtpHandler.ExecuteAsync(Email, WrongCode, ActorTypes.Student, null, dbContext, tokenService, auditLogWriter,
                MasterCodeDisabled(), FakeHostEnvironment.Production(), CancellationToken.None));

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
                VerifyOtpHandler.ExecuteAsync(Email, WrongCode, ActorTypes.Student, null, dbContext, tokenService, auditLogWriter,
                    MasterCodeDisabled(), FakeHostEnvironment.Production(), CancellationToken.None));
        }

        // The 6th attempt — presenting the CORRECT code — must still fail as locked, not succeed.
        await Assert.ThrowsAsync<OtpLockedException>(() =>
            VerifyOtpHandler.ExecuteAsync(Email, Code, ActorTypes.Student, null, dbContext, tokenService, auditLogWriter,
                MasterCodeDisabled(), FakeHostEnvironment.Production(), CancellationToken.None));

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
            VerifyOtpHandler.ExecuteAsync(Email, Code, ActorTypes.Student, null, dbContext, tokenService, auditLogWriter,
                MasterCodeDisabled(), FakeHostEnvironment.Production(), CancellationToken.None));

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
            VerifyOtpHandler.ExecuteAsync(Email, Code, ActorTypes.Student, null, dbContext, tokenService, auditLogWriter,
                MasterCodeDisabled(), FakeHostEnvironment.Production(), CancellationToken.None));

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

        await VerifyOtpHandler.ExecuteAsync(Email, Code, ActorTypes.Student, null, dbContext, tokenService, auditLogWriter,
            MasterCodeDisabled(), FakeHostEnvironment.Production(), CancellationToken.None);

        await Assert.ThrowsAsync<OtpInvalidException>(() =>
            VerifyOtpHandler.ExecuteAsync(Email, Code, ActorTypes.Student, null, dbContext, tokenService, auditLogWriter,
                MasterCodeDisabled(), FakeHostEnvironment.Production(), CancellationToken.None));
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

    // --- Dev-only master-code bypass coverage ---
    //
    // The bypass is a developer-convenience addition: in Development, presenting the configured
    // master code logs the caller in without ever having requested a real OTP. Two independent
    // gates — config presence AND IHostEnvironment.IsDevelopment() — must BOTH be true for the
    // bypass to fire. The five tests below pin every cell of the 2x2 truth table.

    [Fact]
    public async Task ExecuteAsync_NewEmail_WithMasterCodeBypass_InDevelopment_CreatesUserAndIssuesTokens()
    {
        // AC: in Development with MasterCode = "000000" configured, calling with code "000000" for a
        // brand-new email (with a valid actorType, NO OtpCode row seeded at all) succeeds:
        // creates the user and issues tokens, exactly like a correct real code would.
        await using var dbContext = CreateDbContext();
        // Deliberately NO SeedOtpCode — the whole point of the bypass is it skips the request step.
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        var result = await VerifyOtpHandler.ExecuteAsync(
            Email, MasterCode, ActorTypes.Student, userAgent: null, dbContext, tokenService, auditLogWriter,
            MasterCodeEnabled(), FakeHostEnvironment.Development(), CancellationToken.None);

        Assert.True(result.IsNewUser);
        Assert.Equal(Email, result.User.Email);
        Assert.Equal(ActorTypes.Student, result.User.ActorType);
        Assert.Equal(VerificationStatuses.Verified, result.User.VerificationStatus);
        Assert.Equal("fake-access-token", result.Tokens.AccessToken);
        Assert.NotNull(await dbContext.Users.SingleOrDefaultAsync(u => u.Email == Email));
        Assert.Empty(await dbContext.OtpCodes.ToListAsync());
    }

    [Fact]
    public async Task ExecuteAsync_ExistingUser_WithMasterCodeBypass_InDevelopment_IssuesTokens()
    {
        // AC: the same call for an existing user (no actorType needed) also succeeds via the bypass.
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
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        var result = await VerifyOtpHandler.ExecuteAsync(
            Email, MasterCode, actorType: null, userAgent: null, dbContext, tokenService, auditLogWriter,
            MasterCodeEnabled(), FakeHostEnvironment.Development(), CancellationToken.None);

        Assert.False(result.IsNewUser);
        Assert.Equal(ActorTypes.Organization, result.User.ActorType);
        Assert.Equal("fake-access-token", result.Tokens.AccessToken);
    }

    [Fact]
    public async Task ExecuteAsync_WithMasterCodeBypass_InProduction_RejectsAsAnyOtherWrongCode()
    {
        // AC (security-critical): the identical call (code "000000", MasterCode = "000000"
        // configured) but with environment.IsDevelopment() == false is rejected exactly like any
        // other wrong code — throws OtpInvalidException, does not create a user, and the
        // wrong-code audit fires (not the bypass audit).
        await using var dbContext = CreateDbContext();
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        // No OtpCode seeded — must hit the no-pending-code branch the same way the wrong-code
        // call without the bypass would (the bypass should never even have been considered).
        await Assert.ThrowsAsync<OtpInvalidException>(() =>
            VerifyOtpHandler.ExecuteAsync(Email, MasterCode, ActorTypes.Student, null, dbContext, tokenService, auditLogWriter,
                MasterCodeEnabled(), FakeHostEnvironment.Production(), CancellationToken.None));

        Assert.Empty(await dbContext.Users.ToListAsync());
        Assert.DoesNotContain(auditLogWriter.Recorded, e => e.Action == "otp_verify_dev_master_code_used");
    }

    [Fact]
    public async Task ExecuteAsync_MasterCodeCandidate_InDevelopment_ButMasterCodeNotConfigured_RejectsNormally()
    {
        // AC: with environment.IsDevelopment() == true but MasterCode unset (null), the code
        // "000000" is rejected as a normal wrong code — there must be NO accidental universal
        // bypass just from being in Development with no code configured.
        await using var dbContext = CreateDbContext();
        SeedOtpCode(dbContext, Email, Code); // Real pending code, but the candidate is WrongCode/MasterCode.
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        await Assert.ThrowsAsync<OtpInvalidException>(() =>
            VerifyOtpHandler.ExecuteAsync(Email, WrongCode, ActorTypes.Student, null, dbContext, tokenService, auditLogWriter,
                MasterCodeDisabled(), FakeHostEnvironment.Development(), CancellationToken.None));

        Assert.Empty(await dbContext.Users.ToListAsync());
        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal(AuditReasons.OtpWrongCode, entry.MetadataJson);
        Assert.DoesNotContain(auditLogWriter.Recorded, e => e.Action == "otp_verify_dev_master_code_used");
    }

    [Fact]
    public async Task ExecuteAsync_WithMasterCodeBypass_InDevelopment_WritesDistinctAuditEntry()
    {
        // AC: the bypass path writes a distinct, identifiable audit entry so it's never
        // confused with a normal "login_succeeded" row in logs. The bypass MUST emit BOTH a
        // distinguishable marker row AND the normal "login_succeeded" success row — the marker
        // makes the bypass reviewable in audit, the success row keeps the user-flow reconstruction
        // complete (matching the existing convention that every login produces a login_succeeded row).
        await using var dbContext = CreateDbContext();
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        var result = await VerifyOtpHandler.ExecuteAsync(
            Email, MasterCode, ActorTypes.Student, userAgent: null, dbContext, tokenService, auditLogWriter,
            MasterCodeEnabled(), FakeHostEnvironment.Development(), CancellationToken.None);

        var bypassEntry = Assert.Single(
            auditLogWriter.Recorded,
            e => e.Action == "otp_verify_dev_master_code_used");
        Assert.Equal("User", bypassEntry.ResourceType);
        // ResourceId is the email (we don't have a User.Id until the success row is written),
        // mirroring how the no-pending-code and actor-type rows key against the email.
        Assert.Equal(Email, bypassEntry.ResourceId);

        var successEntry = Assert.Single(auditLogWriter.Recorded, e => e.Action == "login_succeeded");
        Assert.Equal(result.User.Id.ToString(), successEntry.ResourceId);
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
