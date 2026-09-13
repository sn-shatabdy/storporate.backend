using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Identity;
using Storporate.Modules.Identity.Exceptions;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Security;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.Identity;

/// <summary>
/// TDD coverage for the refresh-token rotation state machine and the reuse/theft-detection
/// branch — the heart of Phase 3's security model.
/// </summary>
public class RefreshSessionHandlerTests
{
    private const string PlaintextRefreshToken = "initial-refresh-token-aaaaaa";
    private const string UserEmail = "refresher@example.com";

    [Fact]
    public async Task ExecuteAsync_ValidRefreshToken_RotatesSession_AndIssuesNewPair()
    {
        await using var dbContext = CreateDbContext();
        var (user, session) = SeedActiveSession(dbContext);
        var tokenService = new RecordingJwtTokenService(dbContext);

        var tokens = await RefreshSessionHandler.ExecuteAsync(
            PlaintextRefreshToken, null, dbContext, tokenService, CancellationToken.None);

        Assert.NotEqual(PlaintextRefreshToken, tokens.RefreshToken);
        Assert.NotEmpty(tokens.AccessToken);

        // Original session now points at the new one via ReplacedBySessionId; not revoked.
        var reloadedOriginal = await dbContext.Sessions.SingleAsync(s => s.Id == session.Id);
        Assert.NotNull(reloadedOriginal.ReplacedBySessionId);
        Assert.Null(reloadedOriginal.RevokedAt);

        // A new session row exists, sharing the FamilyId.
        var allSessions = await dbContext.Sessions.ToListAsync();
        Assert.Equal(2, allSessions.Count);
        Assert.All(allSessions, s => Assert.Equal(session.FamilyId, s.FamilyId));
    }

    [Fact]
    public async Task ExecuteAsync_ResubmittingAlreadyRotatedRefreshToken_RevokesEntireFamily_AndRejectsSuccessor()
    {
        await using var dbContext = CreateDbContext();
        var (user, originalSession) = SeedActiveSession(dbContext);
        var tokenService = new RecordingJwtTokenService(dbContext);

        // First refresh — rotates out the original token and issues a new one (stored on the
        // tokenService as `tokenService.PlaintextRefreshTokens[1]`).
        await RefreshSessionHandler.ExecuteAsync(
            PlaintextRefreshToken, null, dbContext, tokenService, CancellationToken.None);

        // Re-submit the same now-rotated-out token. The handler must detect this as reuse,
        // revoke the whole family, and throw RefreshTokenReusedException.
        await Assert.ThrowsAsync<RefreshTokenReusedException>(() =>
            RefreshSessionHandler.ExecuteAsync(
                PlaintextRefreshToken, null, dbContext, tokenService, CancellationToken.None));

        // Every session in the family is revoked (both the original and its rotated-in successor).
        var familySessions = await dbContext.Sessions
            .Where(s => s.FamilyId == originalSession.FamilyId)
            .ToListAsync();
        Assert.Equal(2, familySessions.Count);
        Assert.All(familySessions, s => Assert.NotNull(s.RevokedAt));

        // The successor's plaintext (what the attacker might also try) is also rejected as a
        // plain invalid refresh — the family revocation already revoked it, but the next
        // request reads it as a revoked session rather than the reuse path.
        var successorRefreshToken = tokenService.PlaintextRefreshTokens[0];
        await Assert.ThrowsAsync<RefreshTokenInvalidException>(() =>
            RefreshSessionHandler.ExecuteAsync(
                successorRefreshToken, null, dbContext, tokenService, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyRevokedSession_ThrowsRefreshTokenInvalid()
    {
        await using var dbContext = CreateDbContext();
        var (user, session) = SeedActiveSession(dbContext);
        session.RevokedAt = DateTime.UtcNow;
        session.UpdatedAt = session.RevokedAt.Value;
        await dbContext.SaveChangesAsync();

        var tokenService = new RecordingJwtTokenService(dbContext);

        await Assert.ThrowsAsync<RefreshTokenInvalidException>(() =>
            RefreshSessionHandler.ExecuteAsync(
                PlaintextRefreshToken, null, dbContext, tokenService, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_ExpiredSession_ThrowsRefreshTokenInvalid()
    {
        await using var dbContext = CreateDbContext();
        var (user, session) = SeedActiveSession(dbContext);
        session.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await dbContext.SaveChangesAsync();

        var tokenService = new RecordingJwtTokenService(dbContext);

        await Assert.ThrowsAsync<RefreshTokenInvalidException>(() =>
            RefreshSessionHandler.ExecuteAsync(
                PlaintextRefreshToken, null, dbContext, tokenService, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_UnknownRefreshToken_ThrowsRefreshTokenInvalid()
    {
        await using var dbContext = CreateDbContext();
        var tokenService = new RecordingJwtTokenService(dbContext);

        await Assert.ThrowsAsync<RefreshTokenInvalidException>(() =>
            RefreshSessionHandler.ExecuteAsync(
                "never-issued-token", null, dbContext, tokenService, CancellationToken.None));
    }

    private static (User user, Session session) SeedActiveSession(WriteDbContext dbContext)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = UserEmail,
            ActorType = ActorTypes.Student,
            VerificationStatus = VerificationStatuses.Verified,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        var session = new Session
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            HashedRefreshToken = Sha256CodeHasher.Hash(PlaintextRefreshToken),
            FamilyId = Guid.NewGuid(),
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            UserAgent = "test-agent",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        dbContext.Sessions.Add(session);
        dbContext.SaveChanges();
        return (user, session);
    }

    private static WriteDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
