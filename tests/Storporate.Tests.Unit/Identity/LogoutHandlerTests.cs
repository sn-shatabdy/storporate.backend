using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Identity;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Security;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.Identity;

/// <summary>
/// TDD coverage for the single-session logout handler. The caller is represented by a
/// <see cref="ClaimsPrincipal"/> carrying the access token's claims (notably <c>sid</c>),
/// matching how the endpoint receives them via <c>HttpContext.User</c> in production.
/// </summary>
public class LogoutHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_RevokesOnlyTheSessionIdentifiedByTheSidClaim_AndAuditsRevocation()
    {
        await using var dbContext = CreateDbContext();
        var (user, sessionA, sessionB) = SeedTwoSessionsForSameUser(dbContext);
        var auditLogWriter = new FakeAuditLogWriter();

        var caller = BuildPrincipal(user.Id, sessionA.Id);
        await LogoutHandler.ExecuteAsync(caller, dbContext, auditLogWriter, CancellationToken.None);

        var reloadedA = await dbContext.Sessions.SingleAsync(s => s.Id == sessionA.Id);
        var reloadedB = await dbContext.Sessions.SingleAsync(s => s.Id == sessionB.Id);

        Assert.NotNull(reloadedA.RevokedAt);
        Assert.Null(reloadedB.RevokedAt);

        // STOR-63 Phase 2: only the actually-revoked session produces a "session_revoked"
        // row — sessionB is untouched (both in DB and in the audit log). ResourceId is the
        // revoked session's id, which is exactly what Phase 3's "show me a user's recent
        // session revocations" admin query needs.
        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("session_revoked", entry.Action);
        Assert.Equal("Session", entry.ResourceType);
        Assert.Equal(sessionA.Id.ToString(), entry.ResourceId);
    }

    [Fact]
    public async Task ExecuteAsync_IdempotentOnAlreadyRevokedSession_AndRecordsNoAuditRow()
    {
        // STOR-63 Phase 2: re-logout of an already-revoked session records NO audit row —
        // no real event happened (the session was already revoked), so by the "real event
        // happened" invariant Phase 2 pins we deliberately don't double-count the same
        // logical event in the log.
        await using var dbContext = CreateDbContext();
        var (user, sessionA, _) = SeedTwoSessionsForSameUser(dbContext);
        sessionA.RevokedAt = DateTime.UtcNow.AddHours(-1);
        await dbContext.SaveChangesAsync();
        var auditLogWriter = new FakeAuditLogWriter();

        var caller = BuildPrincipal(user.Id, sessionA.Id);
        await LogoutHandler.ExecuteAsync(caller, dbContext, auditLogWriter, CancellationToken.None);

        // No exception, no change to the previously-set RevokedAt timestamp.
        var reloaded = await dbContext.Sessions.SingleAsync(s => s.Id == sessionA.Id);
        Assert.NotNull(reloaded.RevokedAt);

        Assert.Empty(auditLogWriter.Recorded);
    }

    [Fact]
    public async Task ExecuteAsync_CallerWithoutSidClaim_IsNoOp()
    {
        await using var dbContext = CreateDbContext();
        var (user, _, _) = SeedTwoSessionsForSameUser(dbContext);
        var auditLogWriter = new FakeAuditLogWriter();

        var caller = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString())],
            authenticationType: "test"));

        await LogoutHandler.ExecuteAsync(caller, dbContext, auditLogWriter, CancellationToken.None);

        var allSessions = await dbContext.Sessions.ToListAsync();
        Assert.All(allSessions, s => Assert.Null(s.RevokedAt));
        Assert.Empty(auditLogWriter.Recorded);
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
        // STOR-62 Phase 4: WriteDbContext now requires IAccountContext for the global
        // query filter. Logout flow touches Users/Sessions (not IAccountScoped), so an
        // empty AmbientAccountContext satisfies the constructor without affecting query
        // results.
        new(new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options,
            new AmbientAccountContext());
}
