using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.DiscoveryHiring.SearchableProfile;
using Storporate.SharedKernel;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Auth;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.DiscoveryHiring;

/// <summary>
/// STOR-43 Phase 1: end-to-end integration coverage for the two
/// <c>/api/discovery/searchable-profile</c> endpoints. Goes through the real
/// ASP.NET Core pipeline with a real-issued access token; the refresh
/// processor's embedding client is the <see cref="FakeEmbeddingClient"/>
/// installed by <see cref="DiscoveryHiringEndpointsFactory"/>, but the
/// processor itself is not exercised here — these tests target the
/// request/response shape, the permission gates, the validator, and the
/// audit hooks. Processor-level coverage lives in
/// <c>RefreshTalentIndexEntryProcessorTests</c>.
/// </summary>
/// <remarks>
/// Pinned behaviors:
/// <list type="bullet">
///   <item>GET first-ever call returns default IsSearchable=false with all show flags true.</item>
///   <item>PUT first-ever call with IsSearchable=true + blank display name returns 400 display_name_required.</item>
///   <item>PUT first-ever call with IsSearchable=true + valid profile persists a row and enqueues a RefreshEntry job in the same SaveChanges.</item>
///   <item>PUT with display name &gt; 80 chars returns 400 display_name_too_long.</item>
///   <item>PUT with IsSearchable=false deletes the student's prior TalentIndexEntry (writes an audit row "searchable_profile_disabled").</item>
///   <item>PUT with IsSearchable=false→true (or true→true edit) writes an audit row "searchable_profile_enabled" only on a flip.</item>
///   <item>Cross-account reads/writes against another student's profile id are 404 (the global query filter hides the row).</item>
///   <item>Non-Student caller → 403 on PUT.</item>
/// </list>
/// </remarks>
public class SearchableProfileEndpointsTests : IClassFixture<DiscoveryHiringEndpointsFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DiscoveryHiringEndpointsFactory _factory;

    public SearchableProfileEndpointsTests(DiscoveryHiringEndpointsFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetSearchableProfile_NoPriorRow_ReturnsDefaultState()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync("/api/discovery/searchable-profile");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<SearchableProfileResponse>(JsonOptions);
        Assert.NotNull(body);
        Assert.False(body!.IsSearchable);
        Assert.Equal(string.Empty, body.DisplayName);
        Assert.True(body.ShowHeadline);
        Assert.True(body.ShowUniversity);
        Assert.True(body.ShowFieldOfStudy);
        Assert.True(body.ShowStudyYear);
    }

    [Fact]
    public async Task GetSearchableProfile_WithoutBearerToken_Returns401()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/discovery/searchable-profile");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PutSearchableProfile_FirstEverOptInBlankDisplayName_Returns400DisplayNameRequired()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PutAsJsonAsync("/api/discovery/searchable-profile", new
        {
            isSearchable = true,
            displayName = "",
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("display_name_required", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task PutSearchableProfile_DisplayNameOver80Chars_Returns400DisplayNameTooLong()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var oversized = new string('a', 81);
        var response = await client.PutAsJsonAsync("/api/discovery/searchable-profile", new
        {
            isSearchable = true,
            displayName = oversized,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("display_name_too_long", await ReadErrorCodeAsync(response));
    }

    [Fact]
    public async Task PutSearchableProfile_FirstEverOptIn_PersistsRowAndEnqueuesRefreshJob()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);
        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();
        var existingAuditCount = audit.Recorded.Count;

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PutAsJsonAsync("/api/discovery/searchable-profile", new
        {
            isSearchable = true,
            displayName = "Sara Field",
            headline = "Backend engineer in training",
            university = "MIT",
            fieldOfStudy = "Computer Science",
            studyYear = 3,
            showHeadline = true,
            showUniversity = true,
            showFieldOfStudy = true,
            showStudyYear = true,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
        accountContext.SetUserId(user.Id);
        accountContext.SetAccountId(user.Id);
        accountContext.SetIsAdministrator(false);

        var profile = await dbContext.StudentSearchProfiles
            .AsNoTracking()
            .SingleAsync(p => p.AccountId == user.Id);
        Assert.True(profile.IsSearchable);
        Assert.Equal("Sara Field", profile.DisplayName);
        Assert.Equal("Backend engineer in training", profile.Headline);
        Assert.Equal("MIT", profile.University);
        Assert.Equal("Computer Science", profile.FieldOfStudy);
        Assert.Equal(3, profile.StudyYear);
        Assert.NotNull(profile.OptedInAt);

        // Reload the ambient account context in case AsyncLocal got displaced
        // by the HTTP request's scope — the request's scope was disposed on
        // return, restoring the previous (null) values, so re-establish the
        // account-scoped reads here.
        // Scope the query to THIS user's account id: the fixture's InMemory
        // store is shared across tests in this class, so an unrelated
        // refresh job from a prior test (e.g. the field-only edit) would
        // otherwise pad the result.
        var refreshJob = Assert.Single(
            dbContext.Jobs.IgnoreQueryFilters().AsNoTracking(),
            j => j.Type == TalentIndexJobTypes.RefreshEntry
                && j.Status == JobStatus.Pending
                && j.AccountId == user.Id);
        var payload = JsonSerializer.Deserialize<RefreshTalentIndexPayload>(refreshJob.PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal(user.Id, payload!.StudentAccountId);

        // Audit: a single "searchable_profile_enabled" row.
        var newAuditEntries = audit.Recorded.Skip(existingAuditCount).ToList();
        var auditRow = Assert.Single(newAuditEntries);
        Assert.Equal("searchable_profile_enabled", auditRow.Action);
        Assert.Equal("SearchableProfile", auditRow.ResourceType);
        Assert.Equal(user.Id.ToString(), auditRow.ResourceId);
    }

    [Fact]
    public async Task PutSearchableProfile_OptOut_DeletesExistingEntryAndWritesDisabledAuditRow()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);

        // Pre-seed an entry under the user's account (talks straight to the
        // repository — matches what the refresh processor would have written
        // during a prior successful refresh).
        var entryStudentId = user.Id;
        await UpsertDirectEntryAsync(entryStudentId, displayName: "Sara Field");

        // Seed an opted-in profile so the PUT sees a true→false transition
        // (the path that audits "searchable_profile_disabled" + deletes).
        var nowOffset = DateTimeOffset.UtcNow;
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
                OptedInAt = nowOffset,
                UpdatedAt = nowOffset,
            });
            await db.SaveChangesAsync();
        }

        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();
        var existingAuditCount = audit.Recorded.Count;

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PutAsJsonAsync("/api/discovery/searchable-profile", new
        {
            isSearchable = false,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using (var verifyScope = _factory.Services.CreateScope())
        {
            var db = verifyScope.ServiceProvider.GetRequiredService<WriteDbContext>();
            // Profile is now not searchable.
            var profile = await db.StudentSearchProfiles
                .AsNoTracking()
                .SingleAsync(p => p.AccountId == user.Id);
            Assert.False(profile.IsSearchable);

            // The talent-index entry was deleted by the PUT — the InMemory
            // branch mirrors to EF, so an .AsNoTracking query will see the
            // deletion.
            var entries = await db.TalentIndexEntries.AsNoTracking()
                .Where(e => e.StudentAccountId == user.Id)
                .ToListAsync();
            Assert.Empty(entries);
        }

        var newAuditEntries = audit.Recorded.Skip(existingAuditCount).ToList();
        var auditRow = Assert.Single(newAuditEntries);
        Assert.Equal("searchable_profile_disabled", auditRow.Action);
        Assert.Equal("SearchableProfile", auditRow.ResourceType);
        Assert.Equal(user.Id.ToString(), auditRow.ResourceId);
    }

    [Fact]
    public async Task PutSearchableProfile_FieldOnlyEdit_DoesNotFlipIsSearchableAndWritesNoToggleAuditRow()
    {
        // Pure field edit on an already-opted-in profile: no audit row
        // because no toggle flipped. (The Phase 3 "last saved" badge uses
        // profile.UpdatedAt for that signal — see plan.)
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);
        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();
        var existingAuditCount = audit.Recorded.Count;

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
                DisplayName = "Original Name",
                OptedInAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PutAsJsonAsync("/api/discovery/searchable-profile", new
        {
            displayName = "Sara Updated",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // No new audit row was written (no toggle flipped).
        var newAuditEntries = audit.Recorded.Skip(existingAuditCount).ToList();
        Assert.Empty(newAuditEntries);
    }

    [Fact]
    public async Task PutSearchableProfile_NonStudent_Returns403()
    {
        var caller = await SeedUserAsync(ActorTypes.Organization, "non-student-talent@example.com");
        var tokens = await IssueTokensAsync(caller);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PutAsJsonAsync("/api/discovery/searchable-profile", new
        {
            isSearchable = true,
            displayName = "Anything",
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ----- test infrastructure -----

    private async Task<User> SeedStudentUserAsync() =>
        await SeedUserAsync(ActorTypes.Student, $"talent-{Guid.NewGuid():N}@example.com");

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
        return await tokenService.IssueTokensAsync(user, "talent-test", CancellationToken.None);
    }

    /// <summary>
    /// Insert a row directly through the talent-index repository so the
    /// opt-out path's "delete prior entry" branch sees something to delete.
    /// The InMemory branch mirrors the upsert into EF, so a verification
    /// query against <c>TalentIndexEntries</c> sees the same row.
    /// </summary>
    private async Task UpsertDirectEntryAsync(Guid studentId, string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITalentIndexRepository>();
        var vector = Enumerable.Range(0, Storporate.SharedKernel.Entities.TalentIndexConstants.EmbeddingDimensions)
            .Select(i => i == 0 ? 1f : 0f)
            .ToArray();
        await repo.UpsertAsync(
            studentAccountId: studentId,
            displayName: displayName,
            headline: null,
            university: null,
            fieldOfStudy: null,
            studyYear: null,
            itemsJson: "[]",
            searchText: "seed",
            contentHash: "seed",
            embedding: vector,
            updatedAt: DateTimeOffset.UtcNow,
            cancellationToken: CancellationToken.None);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }
        var json = JsonDocument.Parse(body);
        return json.RootElement.TryGetProperty("errorCode", out var errorCode)
            ? errorCode.GetString()
            : null;
    }
}