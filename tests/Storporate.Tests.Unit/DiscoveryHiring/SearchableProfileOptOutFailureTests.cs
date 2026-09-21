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

namespace Storporate.Tests.Unit.DiscoveryHiring;

/// <summary>
/// STOR-43 Cross-Validation follow-up (b): opt-out ordering safety. Replaces
/// the production <see cref="ITalentIndexRepository"/> with a fake whose
/// <c>DeleteAsync</c> always throws, so we can prove
/// <c>UpdateSearchableProfileHandler</c> never commits an
/// <c>IsSearchable=true → false</c> flip when the mirror entry delete fails.
/// </summary>
/// <remarks>
/// Kept in its own class (and its own factory) because the failing fake has
/// to outlive the request scope — switching the real repo's registration
/// would corrupt the other endpoint tests in
/// <see cref="SearchableProfileEndpointsTests"/>.
/// </remarks>
public sealed class SearchableProfileOptOutFailureTests
    : IClassFixture<SearchableProfileOptOutFailureTests.OptOutFailureFactory>
{
    private readonly OptOutFailureFactory _factory;

    public SearchableProfileOptOutFailureTests(OptOutFailureFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PutSearchableProfile_OptOut_DeleteFails_ProfileStaysSearchable()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);

        // Pre-seed an opted-in profile row; the failing fake makes
        // DeleteAsync throw, so the test proves the profile flip never
        // commits even though the handler reaches the opt-out branch.
        using (var seedScope = _factory.Services.CreateScope())
        {
            var db = seedScope.ServiceProvider.GetRequiredService<WriteDbContext>();
            var accountContext = seedScope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
            accountContext.SetUserId(user.Id);
            accountContext.SetAccountId(user.Id);
            accountContext.SetIsAdministrator(false);

            db.StudentSearchProfiles.Add(new StudentSearchProfile
            {
                Id = Guid.NewGuid(),
                AccountId = user.Id,
                IsSearchable = true,
                DisplayName = "Sara Field",
                OptedInAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();
        var failing = _factory.Services.GetRequiredService<FailingTalentIndexRepository>();
        var existingDeleteCalls = failing.DeleteCallCount;
        var existingAuditCount = audit.Recorded.Count;

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PutAsJsonAsync("/api/discovery/searchable-profile", new
        {
            isSearchable = false,
        });

        // The exact wire shape is incidental — what matters for this test
        // is the post-condition on the profile row. The handler lets the
        // inner InvalidOperationException bubble out (currently mapped to
        // 500 by the global exception handler); a future change to that
        // mapping should not affect the opt-out ordering contract this
        // test pins.

        // The fake was actually invoked (otherwise the test is vacuous).
        Assert.Equal(existingDeleteCalls + 1, failing.DeleteCallCount);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var profile = await verifyDb.StudentSearchProfiles
            .AsNoTracking()
            .SingleAsync(p => p.AccountId == user.Id);

        // Critical assertion: the opt-out flip never committed.
        Assert.True(profile.IsSearchable);
        Assert.Equal("Sara Field", profile.DisplayName);

        // No toggle audit row was written either (the handler short-circuits
        // before WriteAuditIfToggledAsync when DeleteAsync throws).
        var newAudit = audit.Recorded.Skip(existingAuditCount).ToList();
        Assert.Empty(newAudit);
    }

    private async Task<User> SeedStudentUserAsync() =>
        await SeedUserAsync(ActorTypes.Student, $"opt-out-fail-{Guid.NewGuid():N}@example.com");

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

    private async Task<AuthTokenResult> IssueTokensAsync(User user)
    {
        using var scope = _factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();
        return await tokenService.IssueTokensAsync(user, "opt-out-fail-test", CancellationToken.None);
    }

    /// <summary>
    /// <see cref="AuthEndpointsFactory"/> derivative that swaps the production
    /// <see cref="ITalentIndexRepository"/> for the failing fake used by this
    /// test class. Also installs the <see cref="FakeAuditLogWriter"/> so we
    /// can inspect the audit list after the PUT fails. The rest of the host
    /// (auth, validators, JWT issuer) inherits from
    /// <see cref="AuthEndpointsFactory"/> unchanged.
    /// </summary>
    public sealed class OptOutFailureFactory : AuthEndpointsFactory
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

                // Mirror the existing DiscoveryHiringEndpointsFactory swap so
                // the test can resolve FakeAuditLogWriter from the same scope.
                services.RemoveAll<IAuditLogWriter>();
                services.AddSingleton<IAuditLogWriter>(_ => AuditLogWriter);
                services.AddSingleton(_ => AuditLogWriter);
            });

            return base.CreateHost(builder);
        }
    }
}
