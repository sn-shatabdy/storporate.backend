using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Identity;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Security;

namespace Storporate.Tests.Unit.Identity;

/// <summary>
/// TDD coverage for the single-session logout handler. The caller is represented by a
/// <see cref="ClaimsPrincipal"/> carrying the access token's claims (notably <c>sid</c>),
/// matching how the endpoint receives them via <c>HttpContext.User</c> in production.
/// </summary>
public class LogoutHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_RevokesOnlyTheSessionIdentifiedByTheSidClaim()
    {
        await using var dbContext = CreateDbContext();
        var (user, sessionA, sessionB) = SeedTwoSessionsForSameUser(dbContext);

        var caller = BuildPrincipal(user.Id, sessionA.Id);
        await LogoutHandler.ExecuteAsync(caller, dbContext, CancellationToken.None);

        var reloadedA = await dbContext.Sessions.SingleAsync(s => s.Id == sessionA.Id);
        var reloadedB = await dbContext.Sessions.SingleAsync(s => s.Id == sessionB.Id);

        Assert.NotNull(reloadedA.RevokedAt);
        Assert.Null(reloadedB.RevokedAt);
    }

    [Fact]
    public async Task ExecuteAsync_IdempotentOnAlreadyRevokedSession()
    {
        await using var dbContext = CreateDbContext();
        var (user, sessionA, _) = SeedTwoSessionsForSameUser(dbContext);
        sessionA.RevokedAt = DateTime.UtcNow.AddHours(-1);
        await dbContext.SaveChangesAsync();

        var caller = BuildPrincipal(user.Id, sessionA.Id);
        await LogoutHandler.ExecuteAsync(caller, dbContext, CancellationToken.None);

        // No exception, no change to the previously-set RevokedAt timestamp.
        var reloaded = await dbContext.Sessions.SingleAsync(s => s.Id == sessionA.Id);
        Assert.NotNull(reloaded.RevokedAt);
    }

    [Fact]
    public async Task ExecuteAsync_CallerWithoutSidClaim_IsNoOp()
    {
        await using var dbContext = CreateDbContext();
        var (user, _, _) = SeedTwoSessionsForSameUser(dbContext);

        var caller = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString())],
            authenticationType: "test"));

        await LogoutHandler.ExecuteAsync(caller, dbContext, CancellationToken.None);

        var allSessions = await dbContext.Sessions.ToListAsync();
        Assert.All(allSessions, s => Assert.Null(s.RevokedAt));
    }

    private static (User user, Session sessionA, Session sessionB) SeedTwoSessionsForSameUser(WriteDbContext dbContext)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "twosessions@example.com",
            ActorType = ActorTypes.Student,
            VerificationStatus = VerificationStatuses.Verified,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var sessionA = new Session
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            HashedRefreshToken = Sha256CodeHasher.Hash("token-a"),
            FamilyId = Guid.NewGuid(),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var sessionB = new Session
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            HashedRefreshToken = Sha256CodeHasher.Hash("token-b"),
            FamilyId = Guid.NewGuid(),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        dbContext.Sessions.AddRange(sessionA, sessionB);
        dbContext.SaveChanges();
        return (user, sessionA, sessionB);
    }

    private static ClaimsPrincipal BuildPrincipal(Guid userId, Guid sessionId) =>
        new(new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, userId.ToString()),
                new Claim(JwtRegisteredClaimNames.Sid, sessionId.ToString()),
            ],
            authenticationType: "test"));

    private static WriteDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
