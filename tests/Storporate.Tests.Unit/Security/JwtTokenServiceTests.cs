using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Security;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Security;

namespace Storporate.Tests.Unit.Security;

public class JwtTokenServiceTests
{
    [Fact]
    public async Task IssueTokensAsync_PersistsHashedRefreshToken_AsNewSessionWithFreshFamilyId()
    {
        await using var dbContext = new WriteDbContext(new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var options = Options.Create(new JwtOptions
        {
            SigningKey = new string('a', 32),
            Issuer = "storporate-tests",
            Audience = "storporate-tests-clients",
        });

        var service = new JwtTokenService(dbContext, options);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "user@example.com",
            ActorType = ActorTypes.Student,
            VerificationStatus = VerificationStatuses.Verified,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var result = await service.IssueTokensAsync(user, userAgent: "xunit-test-agent", CancellationToken.None);

        Assert.False(string.IsNullOrWhiteSpace(result.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(result.RefreshToken));

        var session = await dbContext.Sessions.SingleAsync();
        Assert.Equal(user.Id, session.UserId);
        Assert.Equal(Sha256CodeHasher.Hash(result.RefreshToken), session.HashedRefreshToken);
        Assert.NotEqual(Guid.Empty, session.FamilyId);
        Assert.Null(session.RevokedAt);
        Assert.Null(session.ReplacedBySessionId);
        Assert.Equal("xunit-test-agent", session.UserAgent);
    }

    [Fact]
    public async Task IssueTokensAsync_CalledTwice_ProducesDifferentFamilyIds()
    {
        await using var dbContext = new WriteDbContext(new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

        var options = Options.Create(new JwtOptions
        {
            SigningKey = new string('a', 32),
            Issuer = "storporate-tests",
            Audience = "storporate-tests-clients",
        });

        var service = new JwtTokenService(dbContext, options);
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = "user2@example.com",
            ActorType = ActorTypes.Student,
            VerificationStatus = VerificationStatuses.Verified,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        var first = await service.IssueTokensAsync(user, null, CancellationToken.None);
        var second = await service.IssueTokensAsync(user, null, CancellationToken.None);

        Assert.NotEqual(first.RefreshToken, second.RefreshToken);

        var sessions = await dbContext.Sessions.Where(s => s.UserId == user.Id).ToListAsync();
        Assert.Equal(2, sessions.Count);
        Assert.NotEqual(sessions[0].FamilyId, sessions[1].FamilyId);
    }
}
