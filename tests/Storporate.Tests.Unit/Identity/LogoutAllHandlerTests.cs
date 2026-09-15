using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Identity;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Security;

namespace Storporate.Tests.Unit.Identity;

/// <summary>
/// TDD coverage for the "log out everywhere" handler — must revoke every non-revoked session
/// for the caller, but never touch another user's sessions.
/// </summary>
public class LogoutAllHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_RevokesEveryActiveSession_ForTheCaller()
    {
        await using var dbContext = CreateDbContext();
        var (user, sessions) = SeedUserWithThreeActiveSessions(dbContext);

        var caller = BuildPrincipal(user.Id);
        var revokedCount = await LogoutAllHandler.ExecuteAsync(caller, dbContext, CancellationToken.None);

        Assert.Equal(3, revokedCount);

        var reloaded = await dbContext.Sessions
            .Where(s => s.UserId == user.Id)
            .ToListAsync();
        Assert.All(reloaded, s => Assert.NotNull(s.RevokedAt));
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRevokeOtherUsersSessions()
    {
        await using var dbContext = CreateDbContext();
        var (callerUser, _) = SeedUserWithThreeActiveSessions(dbContext, "caller@example.com");
        var (otherUser, otherSessions) = SeedUserWithThreeActiveSessions(dbContext, "other@example.com");

        var caller = BuildPrincipal(callerUser.Id);
        var revokedCount = await LogoutAllHandler.ExecuteAsync(caller, dbContext, CancellationToken.None);

        // Only the caller's 3 sessions were active — other user's were also active, but they
        // belong to a different user id so they remain untouched.
        Assert.Equal(3, revokedCount);

        var otherReloaded = await dbContext.Sessions
            .Where(s => s.UserId == otherUser.Id)
            .ToListAsync();
        Assert.All(otherReloaded, s => Assert.Null(s.RevokedAt));
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyRevokedSessions_AreNotCounted()
    {
        await using var dbContext = CreateDbContext();
        var (user, sessions) = SeedUserWithThreeActiveSessions(dbContext);

        // Mark one session as already revoked.
        sessions[0].RevokedAt = DateTime.UtcNow.AddHours(-1);
        await dbContext.SaveChangesAsync();

        var caller = BuildPrincipal(user.Id);
        var revokedCount = await LogoutAllHandler.ExecuteAsync(caller, dbContext, CancellationToken.None);

        Assert.Equal(2, revokedCount);
    }

    private static (User user, List<Session> sessions) SeedUserWithThreeActiveSessions(
        WriteDbContext dbContext,
        string email = "logout-all@example.com")
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

        var sessions = new List<Session>();
        for (var i = 0; i < 3; i++)
        {
            var session = new Session
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                HashedRefreshToken = Sha256CodeHasher.Hash($"token-{i}-{Guid.NewGuid()}"),
                FamilyId = Guid.NewGuid(),
                ExpiresAt = DateTime.UtcNow.AddDays(7),
                CreatedAt = DateTime.UtcNow.AddMinutes(-i),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-i),
            };
            dbContext.Sessions.Add(session);
            sessions.Add(session);
        }
        dbContext.SaveChanges();
        return (user, sessions);
    }

    private static ClaimsPrincipal BuildPrincipal(Guid userId) =>
        new(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sub, userId.ToString())],
            authenticationType: "test"));

    private static WriteDbContext CreateDbContext() =>
        // STOR-62 Phase 4: see LogoutHandlerTests comment.
        new(new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options,
            new AmbientAccountContext());
}
