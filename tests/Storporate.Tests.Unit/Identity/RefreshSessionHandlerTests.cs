using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Authorization;
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
    public async Task ExecuteAsync_ValidRefreshToken_RotatesSession_AndIssuesNewPair_AndAuditsSuccess()
    {
        await using var dbContext = CreateDbContext();
        var (user, session) = SeedActiveSession(dbContext);
        var tokenService = new RecordingJwtTokenService(dbContext);
        var auditLogWriter = new FakeAuditLogWriter();

        var tokens = await RefreshSessionHandler.ExecuteAsync(
            PlaintextRefreshToken, null, dbContext, tokenService, auditLogWriter, CancellationToken.None);

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

        // STOR-63 Phase 2: success path audits the rotation outcome with no resourceId
        // (the rotated-out session's id isn't useful for query purposes — only the family
        // is, and that's not yet stable across rotations).
        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("token_refreshed", entry.Action);
        Assert.Equal("Session", entry.ResourceType);
        Assert.Null(entry.ResourceId);
    }

    [Fact]
    public async Task ExecuteAsync_ResubmittingAlreadyRotatedRefreshToken_RevokesEntireFamily_AndRejectsSuccessor_AndAuditsReuse()
    {
        await using var dbContext = CreateDbContext();
        var (user, originalSession) = SeedActiveSession(dbContext);
        var tokenService = new RecordingJwtTokenService(dbContext);
        var auditLogWriter = new FakeAuditLogWriter();

        // First refresh — rotates out the original token and issues a new one (stored on the
        // tokenService as `tokenService.PlaintextRefreshTokens[1]`).
        await RefreshSessionHandler.ExecuteAsync(
            PlaintextRefreshToken, null, dbContext, tokenService, auditLogWriter, CancellationToken.None);

        // Re-submit the same now-rotated-out token. The handler must detect this as reuse,
        // revoke the whole family, and throw RefreshTokenReusedException.
        await Assert.ThrowsAsync<RefreshTokenReusedException>(() =>
            RefreshSessionHandler.ExecuteAsync(
                PlaintextRefreshToken, null, dbContext, tokenService, auditLogWriter, CancellationToken.None));

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
                successorRefreshToken, null, dbContext, tokenService, auditLogWriter, CancellationToken.None));

        // STOR-63 Phase 2: the three refresh attempts produced exactly three audit rows —
        // success, reuse, and the third (revoked) attempt as a plain failure. This is the
        // exact forensic timeline an Integrity Layer (STOR-45) reviewer needs to reconstruct
        // "attacker grabbed the rotated-out refresh token, family got revoked, then the
        // attacker tried the rotated-in one too".
        Assert.Equal(3, auditLogWriter.Recorded.Count);
        Assert.Equal("token_refreshed", auditLogWriter.Recorded[0].Action);
        Assert.Equal("refresh_token_reused", auditLogWriter.Recorded[1].Action);
        Assert.Equal("token_refresh_failed", auditLogWriter.Recorded[2].Action);
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyRevokedSession_ThrowsRefreshTokenInvalid_AndAuditsFailure()
    {
        await using var dbContext = CreateDbContext();
        var (user, session) = SeedActiveSession(dbContext);
        session.RevokedAt = DateTime.UtcNow;
        session.UpdatedAt = session.RevokedAt.Value;
        await dbContext.SaveChangesAsync();

        var tokenService = new RecordingJwtTokenService(dbContext);
        var auditLogWriter = new FakeAuditLogWriter();

        await Assert.ThrowsAsync<RefreshTokenInvalidException>(() =>
            RefreshSessionHandler.ExecuteAsync(
                PlaintextRefreshToken, null, dbContext, tokenService, auditLogWriter, CancellationToken.None));

        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("token_refresh_failed", entry.Action);
    }

    [Fact]
    public async Task ExecuteAsync_ExpiredSession_ThrowsRefreshTokenInvalid_AndAuditsFailure()
    {
        await using var dbContext = CreateDbContext();
        var (user, session) = SeedActiveSession(dbContext);
        session.ExpiresAt = DateTime.UtcNow.AddMinutes(-1);
        await dbContext.SaveChangesAsync();

        var tokenService = new RecordingJwtTokenService(dbContext);
        var auditLogWriter = new FakeAuditLogWriter();

        await Assert.ThrowsAsync<RefreshTokenInvalidException>(() =>
            RefreshSessionHandler.ExecuteAsync(
                PlaintextRefreshToken, null, dbContext, tokenService, auditLogWriter, CancellationToken.None));

        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("token_refresh_failed", entry.Action);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownRefreshToken_ThrowsRefreshTokenInvalid_AndAuditsFailure()
    {
        await using var dbContext = CreateDbContext();
        var tokenService = new RecordingJwtTokenService(dbContext);
        var auditLogWriter = new FakeAuditLogWriter();

        await Assert.ThrowsAsync<RefreshTokenInvalidException>(() =>
            RefreshSessionHandler.ExecuteAsync(
                "never-issued-token", null, dbContext, tokenService, auditLogWriter, CancellationToken.None));

        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("token_refresh_failed", entry.Action);
    }

    [Fact]
    public async Task ExecuteAsync_RotationLosesRace_RevokesEntireFamily_AndThrowsRefreshTokenReused_AndAuditsReuse()
    {
        // Security regression (Cross-Validation, STOR-61): refresh-token rotation has a TOCTOU
        // race where two concurrent refresh calls presenting the exact same still-valid token
        // could both pass the handler's "ReplacedBySessionId is null" check before either write
        // lands, producing two successor sessions and never triggering reuse/theft detection.
        // The fix makes the claim atomic in JwtTokenService.IssueRotatedTokensAsync — when the
        // claim loses, the method returns null and the handler must revoke the entire family
        // and throw RefreshTokenReusedException, identically to the explicit-reuse branch
        // (a raced loser is indistinguishable from a knowing token-reuse attacker from the
        // system's perspective).
        //
        // The InMemory provider used by these unit tests cannot execute the production
        // ExecuteUpdateAsync atomic claim directly, so this test exercises the handler's
        // reaction to the "claim lost" signal via a RaceLosingJwtTokenService fake that
        // mirrors the production null-return behaviour. The atomic-claim correctness in
        // production (where Postgres serialises concurrent UPDATEs) is covered by inspection
        // of JwtTokenService.IssueRotatedTokensAsync's WHERE filter.
        await using var dbContext = CreateDbContext();
        var (user, originalSession) = SeedActiveSession(dbContext);
        var tokenService = new RaceLosingJwtTokenService();
        var auditLogWriter = new FakeAuditLogWriter();

        await Assert.ThrowsAsync<RefreshTokenReusedException>(() =>
            RefreshSessionHandler.ExecuteAsync(
                PlaintextRefreshToken, null, dbContext, tokenService, auditLogWriter, CancellationToken.None));

        // The raced-loser must be treated identically to a knowing token-reuse attacker:
        // the original session is revoked (so it cannot be replayed again) and no new
        // successor session exists (the rotation "lost" produced nothing).
        var reloadedOriginal = await dbContext.Sessions.SingleAsync(s => s.Id == originalSession.Id);
        Assert.NotNull(reloadedOriginal.RevokedAt);

        var familySessions = await dbContext.Sessions
            .Where(s => s.FamilyId == originalSession.FamilyId)
            .ToListAsync();
        Assert.Single(familySessions);
        Assert.Equal(originalSession.Id, familySessions[0].Id);

        Assert.Equal(1, tokenService.IssueRotatedCalls);

        // STOR-63 Phase 2: the race-loser audit row uses "refresh_token_reused" (not
        // "token_refresh_failed") — a raced loser IS a knowing token-reuse attacker from
        // the audit log's point of view. This is the audit row that matters for "did we
        // correctly catch this as a security event" forensic reviews.
        var entry = Assert.Single(auditLogWriter.Recorded);
        Assert.Equal("refresh_token_reused", entry.Action);
        Assert.Equal("Session", entry.ResourceType);
        Assert.Null(entry.ResourceId);
    }

    [Fact]
    public async Task ExecuteAsync_RefreshTokenReused_IsDistinctFromRefreshTokenFailed()
    {
        // STOR-63 Phase 2 acceptance criterion: a dedicated test confirms the
        // reuse/theft branch specifically records "refresh_token_reused", distinct from
        // a plain "token_refresh_failed" test case. The other refresh-failure tests above
        // each pin the "token_refresh_failed" side; this one pins the "refresh_token_reused"
        // side and asserts they're never collapsed into one another.
        await using var dbContext = CreateDbContext();
        var (user, originalSession) = SeedActiveSession(dbContext);
        var tokenService = new RecordingJwtTokenService(dbContext);
        var auditLogWriter = new FakeAuditLogWriter();

        // First call rotates the original session out — establishing that the next
        // presentation of the same token is a reuse event, not a "never-issued" event.
        await RefreshSessionHandler.ExecuteAsync(
            PlaintextRefreshToken, null, dbContext, tokenService, auditLogWriter, CancellationToken.None);

        // Second presentation of the same now-rotated-out token — the reuse branch.
        await Assert.ThrowsAsync<RefreshTokenReusedException>(() =>
            RefreshSessionHandler.ExecuteAsync(
                PlaintextRefreshToken, null, dbContext, tokenService, auditLogWriter, CancellationToken.None));

        // The reuse row must be present and distinct from any token_refresh_failed row.
        var reuseEntry = Assert.Single(auditLogWriter.Recorded, e => e.Action == "refresh_token_reused");
        Assert.Equal("Session", reuseEntry.ResourceType);
        Assert.DoesNotContain(auditLogWriter.Recorded, e => e.Action == "token_refresh_failed");
    }

    [Fact]
    public async Task RecordingJwtTokenService_IssueRotatedTokensAsync_WhenSessionAlreadyClaimed_ReturnsNull()
    {
        // Direct unit test for the in-process equivalent of the production atomic claim:
        // when the original session's ReplacedBySessionId is already set, the service must
        // detect the collision and return null so the handler revokes the family. This is
        // the InMemory-provider analogue of the production ExecuteUpdateAsync returning
        // rowsAffected == 0.
        await using var dbContext = CreateDbContext();
        var (user, originalSession) = SeedActiveSession(dbContext);

        // Simulate the winner's claim having landed.
        originalSession.ReplacedBySessionId = Guid.NewGuid();
        originalSession.UpdatedAt = DateTime.UtcNow;
        await dbContext.SaveChangesAsync();

        var tokenService = new RecordingJwtTokenService(dbContext);

        var result = await tokenService.IssueRotatedTokensAsync(
            user, null, originalSession.FamilyId, originalSession.Id, CancellationToken.None);

        Assert.Null(result);
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
        // STOR-62 Phase 4: see LogoutHandlerTests comment.
        new(new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options,
            new AmbientAccountContext());
}
