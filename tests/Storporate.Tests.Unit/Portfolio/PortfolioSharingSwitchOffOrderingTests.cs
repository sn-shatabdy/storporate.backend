using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Auth;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.Portfolio;

/// <summary>
/// STOR-44 Phase 1: switch-off ordering safety. Mirrors
/// <see cref="Storporate.Tests.Unit.DiscoveryHiring.SearchableProfileOptOutFailureTests"/>'s
/// posture — replace the production <see cref="ITalentIndexRepository"/>
/// with a fake whose <see cref="FailingTalentIndexRepository.ClearOriginalAsync"/>
/// throws, so we can prove <see cref="UpdatePortfolioItemSharingHandler"/> never
/// commits a <c>true → false</c> flip when the descriptor clear fails.
/// </summary>
/// <remarks>
/// <para>
/// The handler's switch-off path runs <c>ClearOriginalAsync</c> BEFORE the
/// flag write (see the handler's own XML comments). The failing fake
/// exercises the "ClearOriginalAsync throws" failure direction. When
/// ClearOriginalAsync throws the handler bubbles the exception; the global
/// exception handler maps it to a non-2xx status. Either way the stored
/// flag stays true and no audit row is written. That's the contract.
/// </para>
/// <para>
/// Kept in its own class (and its own factory) because the failing fake
/// would corrupt the unrelated sharing endpoint tests in
/// <see cref="PortfolioSharingEndpointIntegrationTests"/>.
/// </para>
/// </remarks>
public sealed class PortfolioSharingSwitchOffOrderingTests
    : IClassFixture<PortfolioSharingSwitchOffOrderingTests.SwitchOffFactory>
{
    private readonly SwitchOffFactory _factory;

    public PortfolioSharingSwitchOffOrderingTests(SwitchOffFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PutSharing_FlipTrueToFalse_ClearFails_FlagStaysTrue()
    {
        // Pre-seed: a Student with an item that already has shareOriginal=true.
        // The failing fake makes ClearOriginalAsync throw, so the handler
        // short-circuits before the flag flip and the request fails. The
        // post-condition is that the stored flag stays true.
        var user = await SeedStudentUserAsync();
        SeedSearchableProfile(user.Id, isSearchable: true);
        var itemId = await SeedLinkItemAsync(user.Id, shareOriginal: true);
        var tokens = await IssueTokensAsync(user);

        var failing = _factory.Services.GetRequiredService<FailingTalentIndexRepository>();
        var existingClearCalls = failing.ClearOriginalCallCount;
        var existingAuditCount = _factory.AuditLogWriter.Recorded.Count;

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PutAsJsonAsync(
            $"/api/portfolio/items/{itemId}/sharing",
            new { shareOriginal = false });

        // ClearOriginalAsync was actually attempted (otherwise the test is
        // vacuous — the assertion would pass even if the handler skipped
        // the call entirely).
        Assert.Equal(existingClearCalls + 1, failing.ClearOriginalCallCount);

        // The exact wire shape is incidental — what matters for this test
        // is the post-condition on the stored flag.
        using var verifyScope = _factory.Services.CreateScope();
        var db = verifyScope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var item = await db.PortfolioItems.AsNoTracking()
            .SingleAsync(i => i.Id == itemId);

        // Critical assertion: the flip never committed.
        Assert.True(item.ShareOriginalWithEmployers);

        // No toggle audit row was written either (the handler short-circuits
        // before WriteAsync when ClearOriginalAsync throws).
        var newAudit = _factory.AuditLogWriter.Recorded
            .Skip(existingAuditCount)
            .ToList();
        Assert.Empty(newAudit);

        // Response is the failure shape (status code intentionally not
        // pinned — any non-2xx is acceptable here; the contract being
        // pinned is the post-condition on the stored flag).
        Assert.False(response.IsSuccessStatusCode);
    }

    // ----- infrastructure -----

    private async Task<User> SeedStudentUserAsync() =>
        await SeedUserAsync(ActorTypes.Student, $"switch-off-{Guid.NewGuid():N}@example.com");

    private async Task<User> SeedUserAsync(string actorType, string email)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = email,
            ActorType = actorType,
            VerificationStatus = VerificationStatuses.Verified,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user;
    }

    private void SeedSearchableProfile(Guid accountId, bool isSearchable)
    {
        using var scope = _factory.Services.CreateScope();
        var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
        accountContext.SetUserId(accountId);
        accountContext.SetAccountId(accountId);
        accountContext.SetIsAdministrator(false);
        var db = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        db.StudentSearchProfiles.Add(new StudentSearchProfile
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            IsSearchable = isSearchable,
            DisplayName = isSearchable ? "Seed Student" : string.Empty,
            OptedInAt = isSearchable ? DateTimeOffset.UtcNow : null,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private async Task<Guid> SeedLinkItemAsync(Guid accountId, bool shareOriginal = false)
    {
        using var scope = _factory.Services.CreateScope();
        var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
        accountContext.SetUserId(accountId);
        accountContext.SetAccountId(accountId);
        accountContext.SetIsAdministrator(false);
        var db = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var item = new PortfolioItem
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Label = "Item",
            Category = PortfolioCategories.Document,
            SubmissionType = PortfolioSubmissionTypes.Link,
            ExternalUrl = "https://example.com/x",
            CreatedAt = DateTimeOffset.UtcNow,
            ShareOriginalWithEmployers = shareOriginal,
        };
        db.PortfolioItems.Add(item);
        await db.SaveChangesAsync();
        return item.Id;
    }

    private async Task<AuthTokenResult> IssueTokensAsync(User user)
    {
        using var scope = _factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();
        return await tokenService.IssueTokensAsync(user, "switch-off-test", CancellationToken.None);
    }

    /// <summary>
    /// <see cref="AuthEndpointsFactory"/> derivative. Replaces the production
    /// <see cref="ITalentIndexRepository"/> with the failing fake the safety
    /// test needs, and installs <see cref="FakeAuditLogWriter"/> so the test
    /// can inspect the audit list after the PUT fails.
    /// </summary>
    public sealed class SwitchOffFactory : AuthEndpointsFactory
    {
        public FakeAuditLogWriter AuditLogWriter { get; } = new();
        public FailingTalentIndexRepository FailingRepository { get; } = new();

        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ITalentIndexRepository>();
                services.AddScoped<ITalentIndexRepository>(_ => FailingRepository);
                // Also register the concrete type so the test can resolve
                // the same instance directly to inspect call counts.
                services.AddSingleton(_ => FailingRepository);

                // Mirror the DiscoveryHiringEndpointsFactory swap so the
                // test can resolve FakeAuditLogWriter from the same scope.
                services.RemoveAll<IAuditLogWriter>();
                services.AddSingleton<IAuditLogWriter>(_ => AuditLogWriter);
                services.AddSingleton(_ => AuditLogWriter);
            });

            return base.CreateHost(builder);
        }
    }
}
