using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Identity;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Security;

namespace Storporate.Tests.Unit.Identity;

/// <summary>
/// TDD coverage for <see cref="ListSessionsHandler.ExecuteAsync"/>: returns the caller's
/// non-revoked, non-expired sessions, marks the one matching the <c>sid</c> claim as the
/// current session, and never includes the hashed refresh token itself.
/// </summary>
public class ListSessionsHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsOnlyActiveSessions_ForTheCaller_MarkingCurrent()
    {
        await using var dbContext = CreateDbContext();
        var (user, sessions) = SeedUserWithMixedSessions(dbContext);

        var caller = BuildPrincipal(user.Id, sessions[1].Id);
        var response = await ListSessionsHandler.ExecuteAsync(caller, dbContext, CancellationToken.None);

        Assert.Equal(2, response.Sessions.Count);

        var currentSession = Assert.Single(response.Sessions, s => s.IsCurrent);
        Assert.Equal(sessions[1].Id, currentSession.SessionId);

        // Hashed token is never returned.
        Assert.All(response.Sessions, s => Assert.NotEqual(sessions[0].HashedRefreshToken, s.SessionId.ToString()));
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotReturnSessionsOfOtherUsers()
    {
        await using var dbContext = CreateDbContext();
        var (callerUser, callerSessions) = SeedUserWithMixedSessions(dbContext, "caller@example.com");
        var (_, otherSessions) = SeedUserWithMixedSessions(dbContext, "other@example.com");

        var caller = BuildPrincipal(callerUser.Id, callerSessions[0].Id);
        var response = await ListSessionsHandler.ExecuteAsync(caller, dbContext, CancellationToken.None);

        Assert.All(response.Sessions, s => Assert.Equal(callerUser.Id, callerUser.Id));
        Assert.DoesNotContain(response.Sessions, s => otherSessions.Any(o => o.Id == s.SessionId));
    }

    [Fact]
    public async Task ExecuteAsync_CallerWithoutSubClaim_ReturnsEmpty()
    {
        await using var dbContext = CreateDbContext();
        var (user, sessions) = SeedUserWithMixedSessions(dbContext);

        var caller = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sid, sessions[0].Id.ToString())],
            authenticationType: "test"));

        var response = await ListSessionsHandler.ExecuteAsync(caller, dbContext, CancellationToken.None);

        Assert.Empty(response.Sessions);
    }

    private static (User user, List<Session> sessions) SeedUserWithMixedSessions(
        WriteDbContext dbContext,
        string email = "list-sessions@example.com")
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            ActorType = ActorTypes.Student,
            VerificationStatus = VerificationStatuses.Verified,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);

        // Session 0: active, eligible for listing.
        // Session 1: active, eligible for listing, this one will be marked "current".
        // Session 2: revoked — must NOT appear in the listing.
        // Session 3: expired — must NOT appear in the listing.
        var sessions = new List<Session>
        {
            New(user.Id, "active-a"),
            New(user.Id, "active-b"),
            New(user.Id, "revoked", revoked: true),
            New(user.Id, "expired", expiresAt: DateTime.UtcNow.AddMinutes(-1)),
        };

        dbContext.Sessions.AddRange(sessions);
        dbContext.SaveChanges();
        return (user, sessions);
    }

    private static Session New(Guid userId, string token, bool revoked = false, DateTime? expiresAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            HashedRefreshToken = Sha256CodeHasher.Hash(token),
            FamilyId = Guid.NewGuid(),
            ExpiresAt = expiresAt ?? DateTime.UtcNow.AddDays(7),
            RevokedAt = revoked ? DateTime.UtcNow : null,
            UserAgent = $"agent-for-{token}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

    private static ClaimsPrincipal BuildPrincipal(Guid userId, Guid currentSessionId) =>
        new(new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.Sid, currentSessionId.ToString()),
            ],
            authenticationType: "test"));

    private static WriteDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
