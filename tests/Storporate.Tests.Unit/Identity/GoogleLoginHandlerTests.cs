using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Identity;
using Storporate.Modules.Identity.Exceptions;
using Storporate.SharedKernel.Auditing;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.Identity;

/// <summary>
/// TDD coverage for the Google login handler's get-or-create semantics, mirroring the OTP
/// handler's "ActorType required for new accounts only" rule. <see cref="FakeGoogleIdTokenValidator"/>
/// sidesteps a live Google round-trip — the real-Google AC1 evidence is deferred (see the
/// Phase 3 plan's explicit "Constraint: no Google OAuth app exists yet" note).
/// </summary>
public class GoogleLoginHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_NewSubjectId_CreatesUser_WithProvidedActorType_AndIssuesTokens_AndAuditsSuccess()
    {
        await using var dbContext = CreateDbContext();
        var googleValidator = new FakeGoogleIdTokenValidator
        {
            SubjectId = "google-sub-newuser",
            Email = "newuser@example.com",
        };
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        var result = await GoogleLoginHandler.ExecuteAsync(
            idToken: "test-id-token",
            actorType: ActorTypes.Student,
            userAgent: null,
            dbContext,
            googleValidator,
            tokenService,
            auditLogWriter,
            CancellationToken.None);

        Assert.True(result.IsNewUser);
        Assert.Equal("newuser@example.com", result.User.Email);
        Assert.Equal(ActorTypes.Student, result.User.ActorType);
        Assert.Equal("google-sub-newuser", result.User.GoogleSubjectId);
        Assert.Equal(VerificationStatuses.Verified, result.User.VerificationStatus);
        Assert.NotNull(await dbContext.Users.SingleOrDefaultAsync(u => u.Email == "newuser@example.com"));
        Assert.Equal(1, tokenService.IssueCount);

        // STOR-63 Phase 2: success path audits against the resulting User.Id — same shape
        // as VerifyOtpHandler's "login_succeeded" so a future audit-log query doesn't have
        // to branch on which auth path produced the row.
        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("google_login_succeeded", entry.Action);
        Assert.Equal("User", entry.ResourceType);
        Assert.Equal(result.User.Id.ToString(), entry.ResourceId);
    }

    [Fact]
    public async Task ExecuteAsync_NewSubjectId_WithOrganizationActorType_CreatesUnverifiedUser()
    {
        await using var dbContext = CreateDbContext();
        var googleValidator = new FakeGoogleIdTokenValidator
        {
            SubjectId = "google-sub-organization",
            Email = "organization@example.com",
        };
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        var result = await GoogleLoginHandler.ExecuteAsync(
            idToken: "test-id-token",
            actorType: ActorTypes.Organization,
            userAgent: null,
            dbContext,
            googleValidator,
            tokenService,
            auditLogWriter,
            CancellationToken.None);

        Assert.Equal(VerificationStatuses.Unverified, result.User.VerificationStatus);
    }

    [Fact]
    public async Task ExecuteAsync_NewUser_WithoutActorType_ThrowsActorTypeRequired_AndAuditsFailure()
    {
        await using var dbContext = CreateDbContext();
        var googleValidator = new FakeGoogleIdTokenValidator
        {
            SubjectId = "google-sub-noactor",
            Email = "noactor@example.com",
        };
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        await Assert.ThrowsAsync<ActorTypeRequiredException>(() =>
            GoogleLoginHandler.ExecuteAsync(
                "test-id-token", null, null, dbContext, googleValidator, tokenService, auditLogWriter, CancellationToken.None));

        Assert.Empty(await dbContext.Users.ToListAsync());

        // STOR-63 Phase 2: missing actor_type records the canonical "actor_type_missing"
        // reason so the admin UI's "ActorType rejected" filter matches.
        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("google_login_failed", entry.Action);
        Assert.Equal("User", entry.ResourceType);
        Assert.Null(entry.ResourceId);
        Assert.Equal(AuditReasons.ActorTypeMissing, entry.MetadataJson);
    }

    [Fact]
    public async Task ExecuteAsync_NewUser_WithUnrecognizedActorType_AuditsActorTypeUnrecognizedReason()
    {
        // Regression guard: the unrecognized-actor-type branch (verifying the bug fix that
        // distinguishes "missing" from "unrecognized") must write the canonical
        // "actor_type_unrecognized" reason — NOT the older "actor_type_required" reason,
        // which lied about which branch fired.
        await using var dbContext = CreateDbContext();
        var googleValidator = new FakeGoogleIdTokenValidator
        {
            SubjectId = "google-sub-bad-actor",
            Email = "badactor@example.com",
        };
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        await Assert.ThrowsAsync<ActorTypeRequiredException>(() =>
            GoogleLoginHandler.ExecuteAsync(
                "test-id-token", "NotARealActorType", null, dbContext, googleValidator, tokenService, auditLogWriter, CancellationToken.None));

        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("google_login_failed", entry.Action);
        Assert.Equal(AuditReasons.ActorTypeUnrecognized, entry.MetadataJson);
    }

    [Fact]
    public async Task ExecuteAsync_ExistingUser_WithMatchingSubjectId_DoesNotRequireActorType()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = "returning@example.com",
            ActorType = ActorTypes.Organization,
            GoogleSubjectId = "google-sub-returning",
            VerificationStatus = VerificationStatuses.Unverified,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();

        var googleValidator = new FakeGoogleIdTokenValidator
        {
            SubjectId = "google-sub-returning",
            Email = "returning@example.com",
        };
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        var result = await GoogleLoginHandler.ExecuteAsync(
            "test-id-token", actorType: null, null, dbContext, googleValidator, tokenService, auditLogWriter, CancellationToken.None);

        Assert.False(result.IsNewUser);
        Assert.Equal(ActorTypes.Organization, result.User.ActorType);
    }

    [Fact]
    public async Task ExecuteAsync_OtpOnlyAccountLinkingGoogleForFirstTime_PopulatesGoogleSubjectId()
    {
        // Account was created via email-OTP earlier (GoogleSubjectId == null); now signs in with
        // Google using the same email. The handler must link the new Google subject id rather
        // than throwing ActorTypeRequired or creating a second account.
        await using var dbContext = CreateDbContext();
        var userId = Guid.NewGuid();
        dbContext.Users.Add(new User
        {
            Id = userId,
            Email = "linking@example.com",
            ActorType = ActorTypes.Student,
            GoogleSubjectId = null,
            VerificationStatus = VerificationStatuses.Verified,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();

        var googleValidator = new FakeGoogleIdTokenValidator
        {
            SubjectId = "google-sub-linking",
            Email = "linking@example.com",
            EmailVerified = true,
        };
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        var result = await GoogleLoginHandler.ExecuteAsync(
            "test-id-token", null, null, dbContext, googleValidator, tokenService, auditLogWriter, CancellationToken.None);

        Assert.False(result.IsNewUser);
        Assert.Equal("google-sub-linking", result.User.GoogleSubjectId);
        Assert.Single(await dbContext.Users.ToListAsync());
    }

    [Fact]
    public async Task ExecuteAsync_OtpOnlyAccountLinkingGoogleWithUnverifiedEmail_ThrowsAndDoesNotMutateUser()
    {
        // Security regression (Cross-Validation, STOR-61): if Google returns email_verified=false
        // for a token whose email matches an existing OTP-created account, the handler must NOT
        // auto-link — Google has not confirmed the caller controls this email address, and
        // silently attaching the new Google identity would be an account-takeover vector. The
        // existing user must be left exactly as it was (no GoogleSubjectId written, no
        // UpdatedAt touched) and the sign-in must fail with a clear, distinct exception so the
        // caller is steered back to the email-OTP path they already proved ownership with.
        await using var dbContext = CreateDbContext();
        var userId = Guid.NewGuid();
        var originalUpdatedAt = DateTime.UtcNow.AddMinutes(-5);
        dbContext.Users.Add(new User
        {
            Id = userId,
            Email = "victim@example.com",
            ActorType = ActorTypes.Student,
            GoogleSubjectId = null,
            VerificationStatus = VerificationStatuses.Verified,
            CreatedAt = originalUpdatedAt,
            UpdatedAt = originalUpdatedAt,
        });
        await dbContext.SaveChangesAsync();

        var googleValidator = new FakeGoogleIdTokenValidator
        {
            SubjectId = "google-sub-attacker",
            Email = "victim@example.com",
            EmailVerified = false,
        };
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        await Assert.ThrowsAsync<GoogleEmailNotVerifiedException>(() =>
            GoogleLoginHandler.ExecuteAsync(
                "test-id-token", null, null, dbContext, googleValidator, tokenService, auditLogWriter, CancellationToken.None));

        // No mutation of the existing user record at all.
        var reloaded = await dbContext.Users.SingleAsync(u => u.Email == "victim@example.com");
        Assert.Null(reloaded.GoogleSubjectId);
        Assert.Equal(originalUpdatedAt, reloaded.UpdatedAt);

        // No new user created, no tokens issued.
        Assert.Single(await dbContext.Users.ToListAsync());
        Assert.Equal(0, tokenService.IssueCount);

        // STOR-63 Phase 2: the unverified-email rejection is a distinct audit action — it's
        // the documented account-takeover-attempt signal, not a plain login failure. Phase
        // 3's admin query distinguishes the two via the action string alone.
        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("google_login_rejected_unverified_email", entry.Action);
        Assert.Equal("User", entry.ResourceType);
        Assert.Null(entry.ResourceId);
        Assert.Null(entry.MetadataJson);
    }

    [Fact]
    public async Task ExecuteAsync_GoogleValidatorThrows_WrapsAsGoogleLoginFailed_AndAuditsFailure()
    {
        await using var dbContext = CreateDbContext();
        var googleValidator = new FakeGoogleIdTokenValidator
        {
            ThrowForToken = "bad-token",
        };
        var tokenService = new FakeJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        await Assert.ThrowsAsync<GoogleLoginFailedException>(() =>
            GoogleLoginHandler.ExecuteAsync(
                "bad-token", ActorTypes.Student, null, dbContext, googleValidator, tokenService, auditLogWriter, CancellationToken.None));

        // STOR-63 Phase 2: bad-token / signature-mismatch failure from the validator
        // records a plain "google_login_failed" (no reason metadata) — the validator's
        // message isn't safe to surface as audit metadata (could echo PII from the failed
        // token), so we deliberately skip it.
        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("google_login_failed", entry.Action);
        Assert.Equal("User", entry.ResourceType);
        Assert.Null(entry.ResourceId);
        Assert.Null(entry.MetadataJson);
    }

    [Fact]
    public void ValidActorTypes_DoesNotIncludeAdministrator()
    {
        // Anti-privilege-escalation invariant: a brand-new user must not be able to register
        // as ActorTypes.Administrator via /api/auth/google. Administrators bypass workspace
        // isolation by design (see the STOR-62 plan), so an attack path that minted an
        // Administrator JWT would silently grant cross-account visibility. The allowlist is
        // the only line of defense; this test pins it as a structural invariant so a future
        // refactor that drops the filter is caught at build time rather than after a
        // security incident.
        Assert.DoesNotContain(ActorTypes.Administrator, GoogleLoginHandler.ValidActorTypes);
    }

    private static WriteDbContext CreateDbContext() =>
        // STOR-62 Phase 4: see LogoutHandlerTests comment.
        new(new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options,
            new AmbientAccountContext());
}
