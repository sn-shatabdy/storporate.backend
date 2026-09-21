using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.InstitutionalClubNetwork.ClubProfiles;
using Storporate.Modules.InstitutionalClubNetwork.SponsorshipGoals;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Auth;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.InstitutionalClubNetwork;

/// <summary>
/// STOR-71 end-to-end coverage of both matching directions through the real pipeline. Data is seeded
/// straight into the database with a unique field of study per test, so tests sharing the fixture
/// never overlap each other, and assertions look only at the ids a test seeded.
/// </summary>
public class SponsorshipMatchEndpointsTests : IClassFixture<DiscoveryHiringEndpointsFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DiscoveryHiringEndpointsFactory _factory;

    public SponsorshipMatchEndpointsTests(DiscoveryHiringEndpointsFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Company_SeesPublishedOverlappingClubsOnly_OrderedStrongGoodPartial()
    {
        var tag = Tag();
        var org = await SeedUserAsync(ActorTypes.Organization);
        var goal = await SeedGoalAsync(org, fields: [tag], years: [2], universities: [$"Uni {tag}"], kinds: ["Hackathon"]);

        var strong = await SeedClubAsync(fields: [tag], years: [2], events: [("Campus Hackathon", null)]);
        var good = await SeedClubAsync(fields: [tag], years: [5], university: $"Uni {tag}");
        var partial = await SeedClubAsync(fields: ["Other"], years: [2]);
        var draft = await SeedClubAsync(fields: [tag], years: [2], events: [("Hackathon", null)], status: ClubProfileStatuses.Draft);
        var none = await SeedClubAsync(fields: ["Other"], years: [6]);

        using var client = await ClientForAsync(org);
        var response = await client.GetAsync($"/api/sponsorship/goals/{goal}/club-matches");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var items = Parse(body).Where(i => new[] { strong, good, partial, draft, none }.Contains(i.Club)).ToList();

        Assert.Equal(new[] { strong, good, partial }, items.Select(i => i.Club).ToArray());
        Assert.Equal(new[] { "Strong", "Good", "Partial" }, items.Select(i => i.Fit).ToArray());
        Assert.Contains(items[0].Reasons, r => r == "They run hackathons, an event kind you would back.");
        Assert.Contains(items[0].Reasons, r => r == $"Their audience includes {tag}, which you want to reach.");
        Assert.Contains(items[2].Reasons, r => r == "They reach Year 2 students.");
        Assert.All(items, i => Assert.InRange(i.Reasons.Length, 1, 4));
        Assert.DoesNotContain(draft, items.Select(i => i.Club));
        Assert.DoesNotContain(none, items.Select(i => i.Club));
        AssertNoNumbersOrAccountData(body, org);
    }

    [Fact]
    public async Task Company_ClubItemsUseTheSameSummaryShapeAsTheClubList()
    {
        var tag = Tag();
        var org = await SeedUserAsync(ActorTypes.Organization);
        var goal = await SeedGoalAsync(org, fields: [tag]);
        var clubId = await SeedClubAsync(fields: [tag]);

        using var client = await ClientForAsync(org);
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/sponsorship/goals/{goal}/club-matches"));
        var item = doc.RootElement.GetProperty("items").EnumerateArray().First(i => i.GetProperty("club").GetProperty("id").GetGuid() == clubId);
        var names = item.GetProperty("club").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "id", "name", "tagline", "university", "memberCount", "fieldsOfStudy", "eventCount" }, names);
        Assert.Equal(new[] { "fit", "reasons", "club" }, item.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Company_CanOnlyMatchItsOwnGoal_ForeignOrMissingIs404()
    {
        var tag = Tag();
        var owner = await SeedUserAsync(ActorTypes.Organization);
        var other = await SeedUserAsync(ActorTypes.Organization);
        var goal = await SeedGoalAsync(owner, fields: [tag]);

        using var otherClient = await ClientForAsync(other);
        var foreign = await otherClient.GetAsync($"/api/sponsorship/goals/{goal}/club-matches");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal("sponsorship_goal_not_found", await ErrorCodeAsync(foreign));

        var missing = await otherClient.GetAsync($"/api/sponsorship/goals/{Guid.NewGuid()}/club-matches");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("sponsorship_goal_not_found", await ErrorCodeAsync(missing));
    }

    [Fact]
    public async Task Company_PausedGoalIsStillMatchableByItsOwner()
    {
        var tag = Tag();
        var org = await SeedUserAsync(ActorTypes.Organization);
        var goal = await SeedGoalAsync(org, fields: [tag], status: SponsorshipGoalStatuses.Paused);
        var clubId = await SeedClubAsync(fields: [tag]);

        using var client = await ClientForAsync(org);
        var items = Parse(await client.GetStringAsync($"/api/sponsorship/goals/{goal}/club-matches"));
        Assert.Contains(clubId, items.Select(i => i.Club));
    }

    [Fact]
    public async Task Company_Query_NarrowsClubsAndAddsSearchReason()
    {
        var tag = Tag();
        var org = await SeedUserAsync(ActorTypes.Organization);
        var goal = await SeedGoalAsync(org, fields: [tag]);
        var withHackathon = await SeedClubAsync(fields: [tag], events: [("Grand Hackathon", null)]);
        var withoutHackathon = await SeedClubAsync(fields: [tag], events: [("Book club", null)]);
        var word = $"chess{Guid.NewGuid():N}";
        var noOverlap = await SeedClubAsync(name: $"Club {word}", fields: ["Elsewhere"]);

        using var client = await ClientForAsync(org);
        var narrowed = Parse(await client.GetStringAsync($"/api/sponsorship/goals/{goal}/club-matches?q=hackathon"));
        var mine = narrowed.Where(i => new[] { withHackathon, withoutHackathon }.Contains(i.Club)).ToList();
        Assert.Equal(new[] { withHackathon }, mine.Select(i => i.Club).ToArray());
        Assert.Equal("Matches your words: hackathon.", mine[0].Reasons[0]);
        Assert.Contains($"Their audience includes {tag}, which you want to reach.", mine[0].Reasons);

        // A club the words match but the goal does not overlap is still returned, as Partial.
        var byName = Parse(await client.GetStringAsync($"/api/sponsorship/goals/{goal}/club-matches?q={word}"));
        var hit = Assert.Single(byName, i => i.Club == noOverlap);
        Assert.Equal("Partial", hit.Fit);
        Assert.Equal($"Matches your words: {word}.", Assert.Single(hit.Reasons));

        // A search of only stop words is treated as no search.
        var stop = Parse(await client.GetStringAsync($"/api/sponsorship/goals/{goal}/club-matches?q=the%20and"));
        Assert.Contains(withoutHackathon, stop.Select(i => i.Club));
        Assert.DoesNotContain(noOverlap, stop.Select(i => i.Club));
    }

    [Fact]
    public async Task Company_Query_HonoursYearMention()
    {
        var tag = Tag();
        var org = await SeedUserAsync(ActorTypes.Organization);
        var goal = await SeedGoalAsync(org, fields: [tag]);
        var year2 = await SeedClubAsync(fields: [tag], years: [2]);
        var year4 = await SeedClubAsync(fields: [tag], years: [4]);

        using var client = await ClientForAsync(org);
        var items = Parse(await client.GetStringAsync($"/api/sponsorship/goals/{goal}/club-matches?q=2nd%20year"));
        var ids = items.Select(i => i.Club).ToArray();
        Assert.Contains(year2, ids);
        Assert.DoesNotContain(year4, ids);
    }

    [Fact]
    public async Task Query_LongerThan200Characters_Returns400_OnBothSides()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var goal = await SeedGoalAsync(org, fields: [Tag()]);
        var club = await SeedUserAsync(ActorTypes.Club);
        await SeedClubAsync(owner: club, fields: [Tag()]);
        var tooLong = new string('a', 201);

        using var orgClient = await ClientForAsync(org);
        using var clubClient = await ClientForAsync(club);
        foreach (var response in new[]
        {
            await orgClient.GetAsync($"/api/sponsorship/goals/{goal}/club-matches?q={tooLong}"),
            await clubClient.GetAsync($"/api/clubs/profile/company-matches?q={tooLong}"),
        })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("sponsorship_match_query_invalid", await ErrorCodeAsync(response));
        }

        Assert.Equal(HttpStatusCode.OK, (await orgClient.GetAsync($"/api/sponsorship/goals/{goal}/club-matches?q={new string('a', 200)}")).StatusCode);
    }

    [Fact]
    public async Task Club_NeedsAPublishedProfile_404WhenNone_409WhenDraft()
    {
        var noProfile = await SeedUserAsync(ActorTypes.Club);
        using var noProfileClient = await ClientForAsync(noProfile);
        var none = await noProfileClient.GetAsync("/api/clubs/profile/company-matches");
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        Assert.Equal("club_profile_not_found", await ErrorCodeAsync(none));

        var draftOwner = await SeedUserAsync(ActorTypes.Club);
        await SeedClubAsync(owner: draftOwner, fields: [Tag()], status: ClubProfileStatuses.Draft);
        using var draftClient = await ClientForAsync(draftOwner);
        var draft = await draftClient.GetAsync("/api/clubs/profile/company-matches");
        Assert.Equal(HttpStatusCode.Conflict, draft.StatusCode);
        Assert.Equal("club_profile_not_published", await ErrorCodeAsync(draft));
    }

    [Fact]
    public async Task Club_SeesActiveGoalsOnly_OrderedByBand_WithClubViewReasons()
    {
        var tag = Tag();
        var org = await SeedUserAsync(ActorTypes.Organization);
        var strong = await SeedGoalAsync(org, fields: [tag], years: [2], kinds: ["Hackathon"]);
        var good = await SeedGoalAsync(org, fields: [tag], years: [6]);
        var partial = await SeedGoalAsync(org, fields: ["Other"], years: [2]);
        var paused = await SeedGoalAsync(org, fields: [tag], years: [2], kinds: ["Hackathon"], status: SponsorshipGoalStatuses.Paused);
        var none = await SeedGoalAsync(org, fields: ["Other"], years: [5]);

        var club = await SeedUserAsync(ActorTypes.Club);
        await SeedClubAsync(owner: club, fields: [tag], years: [2, 6], events: [("Hackathon", null)]);

        using var client = await ClientForAsync(club);
        var response = await client.GetAsync("/api/clubs/profile/company-matches");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var items = Parse(body, "company").Where(i => new[] { strong, good, partial, paused, none }.Contains(i.Club)).ToList();

        Assert.Equal(new[] { strong, good, partial }, items.Select(i => i.Club).ToArray());
        Assert.Equal(new[] { "Strong", "Good", "Partial" }, items.Select(i => i.Fit).ToArray());
        Assert.Contains($"They want to reach {tag} students, and your audience includes them.", items[0].Reasons);
        Assert.Contains("They back hackathons, and you run one.", items[0].Reasons);
        AssertNoNumbersOrAccountData(body, club, org);
    }

    [Fact]
    public async Task Club_NestedCompanySummary_HidesBudgetUnlessVisibleAndBounded()
    {
        var tag = Tag();
        var org = await SeedUserAsync(ActorTypes.Organization);
        var shown = await SeedGoalAsync(org, fields: [tag], budget: (50_000, 200_000, true));
        var hidden = await SeedGoalAsync(org, fields: [tag], budget: (50_000, 200_000, false));
        var noBound = await SeedGoalAsync(org, fields: [tag], budget: (null, null, true));
        var club = await SeedUserAsync(ActorTypes.Club);
        await SeedClubAsync(owner: club, fields: [tag]);

        using var client = await ClientForAsync(club);
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/clubs/profile/company-matches"));
        JsonElement Company(Guid id) => doc.RootElement.GetProperty("items").EnumerateArray()
            .First(i => i.GetProperty("company").GetProperty("id").GetGuid() == id).GetProperty("company");

        var budget = Company(shown).GetProperty("budget");
        Assert.Equal(50_000, budget.GetProperty("min").GetInt32());
        Assert.Equal(200_000, budget.GetProperty("max").GetInt32());
        Assert.Equal(JsonValueKind.Null, Company(hidden).GetProperty("budget").ValueKind);
        Assert.Equal(JsonValueKind.Null, Company(noBound).GetProperty("budget").ValueKind);
    }

    [Fact]
    public async Task Club_Query_NarrowsCompaniesAndAddsSearchReason()
    {
        var tag = Tag();
        var org = await SeedUserAsync(ActorTypes.Organization);
        var word = $"zebra{Guid.NewGuid():N}";
        var byName = await SeedGoalAsync(org, name: $"Campaign {word}", fields: ["Elsewhere"]);
        var overlapOnly = await SeedGoalAsync(org, fields: [tag]);
        var club = await SeedUserAsync(ActorTypes.Club);
        await SeedClubAsync(owner: club, fields: [tag]);

        using var client = await ClientForAsync(club);
        var items = Parse(await client.GetStringAsync($"/api/clubs/profile/company-matches?q={word}"), "company");
        var hit = Assert.Single(items, i => i.Club == byName);
        Assert.Equal("Partial", hit.Fit);
        Assert.Equal($"Matches your words: {word}.", Assert.Single(hit.Reasons));
        Assert.DoesNotContain(overlapOnly, items.Select(i => i.Club));
    }

    [Fact]
    public async Task Authorization_EachSideOnlyReachesItsOwnEndpoint()
    {
        var goalId = Guid.NewGuid();
        using var student = await ClientForAsync(await SeedUserAsync(ActorTypes.Student));
        using var org = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        using var club = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        using var anonymous = _factory.CreateClient();

        var companyUrl = $"/api/sponsorship/goals/{goalId}/club-matches";
        const string clubUrl = "/api/clubs/profile/company-matches";

        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync(companyUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await student.GetAsync(clubUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(companyUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(clubUrl)).StatusCode);

        // The permission is granted to both roles, so each endpoint also checks the caller's actor type.
        Assert.Equal(HttpStatusCode.Forbidden, (await club.GetAsync(companyUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await org.GetAsync(clubUrl)).StatusCode);
    }

    private static string Tag() => $"Field{Guid.NewGuid():N}";

    private sealed record Item(string Fit, string[] Reasons, Guid Club);

    private static List<Item> Parse(string body, string nested = "club")
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(i => new Item(
                i.GetProperty("fit").GetString()!,
                i.GetProperty("reasons").EnumerateArray().Select(r => r.GetString()!).ToArray(),
                i.GetProperty(nested).GetProperty("id").GetGuid()))
            .ToList();
    }

    private static void AssertNoNumbersOrAccountData(string body, params User[] users)
    {
        Assert.DoesNotContain("score", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("percent", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("points", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ownerAccountId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", body, StringComparison.OrdinalIgnoreCase);
        foreach (var user in users)
        {
            Assert.DoesNotContain(user.Id.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(user.Email, body, StringComparison.OrdinalIgnoreCase);
        }

        using var doc = JsonDocument.Parse(body);
        foreach (var item in doc.RootElement.GetProperty("items").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, item.GetProperty("fit").ValueKind);
        }
    }

    private async Task<Guid> SeedGoalAsync(
        User owner,
        string[]? fields = null,
        int[]? years = null,
        string[]? universities = null,
        string[]? kinds = null,
        string status = SponsorshipGoalStatuses.Active,
        string? name = null,
        (int? Min, int? Max, bool Visible)? budget = null)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var now = DateTimeOffset.UtcNow;
        var goal = new SponsorshipGoalSet
        {
            Id = Guid.NewGuid(),
            OwnerAccountId = owner.Id,
            Name = name ?? "Campus push",
            CompanyName = "Acme Ltd",
            Objectives = SponsorshipGoalOptions.Serialize(new[] { "Brand awareness" }),
            AudienceFieldsOfStudy = SponsorshipGoalOptions.Serialize(fields ?? []),
            AudienceYears = SponsorshipGoalOptions.Serialize(years ?? []),
            AudienceCities = "[]",
            AudienceUniversities = SponsorshipGoalOptions.Serialize(universities ?? []),
            EventKinds = SponsorshipGoalOptions.Serialize(kinds ?? []),
            BudgetMin = budget?.Min,
            BudgetMax = budget?.Max,
            ShowBudget = budget?.Visible ?? false,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.SponsorshipGoalSets.Add(goal);
        await dbContext.SaveChangesAsync();
        return goal.Id;
    }

    private async Task<Guid> SeedClubAsync(
        User? owner = null,
        string? name = null,
        string[]? fields = null,
        int[]? years = null,
        string university = "Some University",
        (string Title, string? Description)[]? events = null,
        string status = ClubProfileStatuses.Published)
    {
        owner ??= await SeedUserAsync(ActorTypes.Club);
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var now = DateTimeOffset.UtcNow;
        var clubEvents = (events ?? []).Select(e => new ClubEvent(Guid.NewGuid(), e.Title, e.Description, 50, "Termly", ["Funding"])).ToList();
        var profile = new ClubProfile
        {
            Id = Guid.NewGuid(),
            OwnerAccountId = owner.Id,
            Name = name ?? "Tech Club",
            About = "A student club that meets every week.",
            University = university,
            MemberCount = 100,
            AudienceFieldsOfStudy = JsonSerializer.Serialize(fields ?? [], ClubProfileOptions.Json),
            AudienceYears = JsonSerializer.Serialize(years ?? [1], ClubProfileOptions.Json),
            EventsJson = JsonSerializer.Serialize(clubEvents, ClubProfileOptions.Json),
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            PublishedAt = status == ClubProfileStatuses.Published ? now : null,
        };
        dbContext.ClubProfiles.Add(profile);
        await dbContext.SaveChangesAsync();
        return profile.Id;
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("errorCode").GetString();
    }

    private async Task<HttpClient> ClientForAsync(User user)
    {
        using var scope = _factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();
        var tokens = await tokenService.IssueTokensAsync(user, "sponsorship-match-test", CancellationToken.None);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private async Task<User> SeedUserAsync(string actorType)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"{actorType.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com",
            ActorType = actorType,
            VerificationStatus = VerificationStatuses.Verified,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync();
        return user;
    }
}
