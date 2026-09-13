using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Identity;
using Storporate.Modules.Identity.Exceptions;
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
    public async Task ExecuteAsync_NewSubjectId_CreatesUser_WithProvidedActorType_AndIssuesTokens()
    {
        await using var dbContext = CreateDbContext();
        var googleValidator = new FakeGoogleIdTokenValidator
        {
            SubjectId = "google-sub-newuser",
            Email = "newuser@example.com",
        };
        var tokenService = new FakeJwtTokenService();

        var result = await GoogleLoginHandler.ExecuteAsync(
            idToken: "test-id-token",
            actorType: ActorTypes.Student,
            userAgent: null,
            dbContext,
            googleValidator,
            tokenService,
            CancellationToken.None);

        Assert.True(result.IsNewUser);
        Assert.Equal("newuser@example.com", result.User.Email);
        Assert.Equal(ActorTypes.Student, result.User.ActorType);
        Assert.Equal("google-sub-newuser", result.User.GoogleSubjectId);
        Assert.Equal(VerificationStatuses.Verified, result.User.VerificationStatus);
        Assert.NotNull(await dbContext.Users.SingleOrDefaultAsync(u => u.Email == "newuser@example.com"));
        Assert.Equal(1, tokenService.IssueCount);
    }

    [Fact]
    public async Task ExecuteAsync_NewSubjectId_WithEmployerActorType_CreatesUnverifiedUser()
    {
        await using var dbContext = CreateDbContext();
        var googleValidator = new FakeGoogleIdTokenValidator
        {
            SubjectId = "google-sub-employer",
            Email = "employer@example.com",
        };
        var tokenService = new FakeJwtTokenService();

        var result = await GoogleLoginHandler.ExecuteAsync(
            idToken: "test-id-token",
            actorType: ActorTypes.Employer,
            userAgent: null,
            dbContext,
            googleValidator,
            tokenService,
            CancellationToken.None);

        Assert.Equal(VerificationStatuses.Unverified, result.User.VerificationStatus);
    }

    [Fact]
    public async Task ExecuteAsync_NewUser_WithoutActorType_ThrowsActorTypeRequired()
    {
        await using var dbContext = CreateDbContext();
        var googleValidator = new FakeGoogleIdTokenValidator
        {
            SubjectId = "google-sub-noactor",
            Email = "noactor@example.com",
        };
        var tokenService = new FakeJwtTokenService();

        await Assert.ThrowsAsync<ActorTypeRequiredException>(() =>
            GoogleLoginHandler.ExecuteAsync(
                "test-id-token", null, null, dbContext, googleValidator, tokenService, CancellationToken.None));

        Assert.Empty(await dbContext.Users.ToListAsync());
    }

    [Fact]
    public async Task ExecuteAsync_ExistingUser_WithMatchingSubjectId_DoesNotRequireActorType()
    {
        await using var dbContext = CreateDbContext();
        dbContext.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            Email = "returning@example.com",
            ActorType = ActorTypes.Employer,
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

        var result = await GoogleLoginHandler.ExecuteAsync(
            "test-id-token", actorType: null, null, dbContext, googleValidator, tokenService, CancellationToken.None);

        Assert.False(result.IsNewUser);
        Assert.Equal(ActorTypes.Employer, result.User.ActorType);
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
        };
        var tokenService = new FakeJwtTokenService();

        var result = await GoogleLoginHandler.ExecuteAsync(
            "test-id-token", null, null, dbContext, googleValidator, tokenService, CancellationToken.None);

        Assert.False(result.IsNewUser);
        Assert.Equal("google-sub-linking", result.User.GoogleSubjectId);
        Assert.Single(await dbContext.Users.ToListAsync());
    }

    [Fact]
    public async Task ExecuteAsync_GoogleValidatorThrows_WrapsAsGoogleLoginFailed()
    {
        await using var dbContext = CreateDbContext();
        var googleValidator = new FakeGoogleIdTokenValidator
        {
            ThrowForToken = "bad-token",
        };
        var tokenService = new FakeJwtTokenService();

        await Assert.ThrowsAsync<GoogleLoginFailedException>(() =>
            GoogleLoginHandler.ExecuteAsync(
                "bad-token", ActorTypes.Student, null, dbContext, googleValidator, tokenService, CancellationToken.None));
    }

    private static WriteDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
