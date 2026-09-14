using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Security;

namespace Storporate.Tests.Unit.Fakes;

/// <summary>
/// <see cref="IJwtTokenService"/> that performs the real DB-side rotation work — needed by
/// refresh/logout/me/sessions tests so the success of one step (inserting the rotated
/// successor session) is observable by the next step (looking it up to detect reuse).
///
/// Plain access tokens are still synthetic strings; only the persistence side effects are real.
/// </summary>
public sealed class RecordingJwtTokenService(WriteDbContext dbContext) : IJwtTokenService
{
    private readonly List<AuthTokenResult> _issued = [];
    private int _issueCount;

    public IReadOnlyList<AuthTokenResult> Issued => _issued;

    /// <summary>All plaintext refresh tokens ever issued, in order, so tests can exercise a
    /// rotated-out token by picking the right one off the list.</summary>
    public List<string> PlaintextRefreshTokens { get; } = [];

    public Task<AuthTokenResult> IssueTokensAsync(
        User user,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var refreshToken = $"recording-refresh-{++_issueCount}";
        PlaintextRefreshTokens.Add(refreshToken);

        var session = new Session
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            HashedRefreshToken = Sha256CodeHasher.Hash(refreshToken),
            FamilyId = Guid.NewGuid(),
            ExpiresAt = now.AddDays(7),
            UserAgent = userAgent,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.Sessions.Add(session);
        dbContext.SaveChanges();

        var result = new AuthTokenResult(
            AccessToken: $"recording-access-{_issueCount}",
            AccessTokenExpiresAt: now.AddMinutes(15),
            RefreshToken: refreshToken,
            RefreshTokenExpiresAt: now.AddDays(7));
        _issued.Add(result);
        return Task.FromResult(result);
    }

    public async Task<AuthTokenResult?> IssueRotatedTokensAsync(
        User user,
        string? userAgent,
        Guid familyId,
        Guid replacedSessionId,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var refreshToken = $"recording-refresh-rotated-{++_issueCount}";
        PlaintextRefreshTokens.Add(refreshToken);

        var newSession = new Session
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            HashedRefreshToken = Sha256CodeHasher.Hash(refreshToken),
            FamilyId = familyId,
            ExpiresAt = now.AddDays(7),
            UserAgent = userAgent,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var replacedSession = await dbContext.Sessions.FirstOrDefaultAsync(
            s => s.Id == replacedSessionId,
            cancellationToken);
        if (replacedSession is null)
        {
            throw new InvalidOperationException(
                $"Session to be replaced ({replacedSessionId}) was not found.");
        }

        // Mirrors JwtTokenService.IssueRotatedTokensAsync's atomic-claim semantics (with the
        // same InMemory caveat: this is a read-then-set, not a true SQL UPDATE, so it does not
        // itself prevent concurrent racing — the handler-level test for the race-loss branch
        // exercises it by pre-setting ReplacedBySessionId, not by racing real threads).
        if (replacedSession.ReplacedBySessionId is not null)
        {
            return null;
        }

        replacedSession.ReplacedBySessionId = newSession.Id;
        replacedSession.UpdatedAt = now;

        dbContext.Sessions.Add(newSession);
        await dbContext.SaveChangesAsync(cancellationToken);

        var result = new AuthTokenResult(
            AccessToken: $"recording-access-rotated-{_issueCount}",
            AccessTokenExpiresAt: now.AddMinutes(15),
            RefreshToken: refreshToken,
            RefreshTokenExpiresAt: now.AddDays(7));
        _issued.Add(result);
        return result;
    }
}
