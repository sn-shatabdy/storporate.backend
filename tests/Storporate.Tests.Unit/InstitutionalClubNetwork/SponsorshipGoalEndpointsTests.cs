using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.InstitutionalClubNetwork.SponsorshipGoals;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Auth;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.InstitutionalClubNetwork;

/// <summary>
/// STOR-70 end-to-end coverage of the company goal endpoints (<c>/api/sponsorship/goals</c>) and the
/// club read endpoints (<c>/api/sponsorship/companies</c>) through the real pipeline with real-issued tokens.
/// </summary>
public class SponsorshipGoalEndpointsTests : IClassFixture<DiscoveryHiringEndpointsFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DiscoveryHiringEndpointsFactory _factory;

    public SponsorshipGoalEndpointsTests(DiscoveryHiringEndpointsFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Create_Returns201WithNormalizedActiveSet_ThenGetListUpdateDelete()
    {
        using var client = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        var body = ValidBody("  Brand push  ");
        body["objectives"] = new[] { "Brand awareness", "brand awareness", "CSR education" };
        body["eventKinds"] = new[] { "Hackathon", "hackathon", "Workshop" };
        body["notes"] = "  Prefer weekends ";
        body["budget"] = new { min = 50_000, max = 200_000, visibleToClubs = true };

        var post = await client.PostAsJsonAsync("/api/sponsorship/goals", body);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = (await post.Content.ReadFromJsonAsync<SponsorshipGoalSetResponse>(JsonOptions))!;
        Assert.Equal("Brand push", created.Name);
        Assert.Equal("Acme Ltd", created.CompanyName);
        Assert.Equal("Active", created.Status);
        Assert.Equal(new[] { "Brand awareness", "CSR education" }, created.Objectives);
        Assert.Equal(new[] { "Hackathon", "Workshop" }, created.EventKinds);
        Assert.Equal(new[] { "Computer Science", "Engineering" }, created.Audience.FieldsOfStudy);
        Assert.Equal(new[] { 1, 2, 3 }, created.Audience.Years);
        Assert.Equal(new[] { "Dhaka" }, created.Audience.Cities);
        Assert.Equal(new[] { "BUET" }, created.Audience.Universities);
        Assert.Equal("Prefer weekends", created.Notes);
        Assert.Equal(new SponsorshipOwnBudgetResponse(50_000, 200_000, true), created.Budget);

        var get = (await client.GetFromJsonAsync<SponsorshipGoalSetResponse>($"/api/sponsorship/goals/{created.Id}", JsonOptions))!;
        Assert.Equal(created.Id, get.Id);

        // Wire shape is camelCase.
        using var doc = JsonDocument.Parse(await client.GetStringAsync($"/api/sponsorship/goals/{created.Id}"));
        Assert.Equal(3, doc.RootElement.GetProperty("audience").GetProperty("years").GetArrayLength());
        Assert.True(doc.RootElement.GetProperty("budget").GetProperty("visibleToClubs").GetBoolean());
        Assert.Equal("Active", doc.RootElement.GetProperty("status").GetString());

        var update = ValidBody("Renamed");
        update["objectives"] = new[] { "Recruiting" };
        update["budget"] = new { min = (int?)null, max = (int?)null, visibleToClubs = false };
        update["notes"] = null;
        var put = await client.PutAsJsonAsync($"/api/sponsorship/goals/{created.Id}", update);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = (await put.Content.ReadFromJsonAsync<SponsorshipGoalSetResponse>(JsonOptions))!;
        Assert.Equal(created.Id, updated.Id);
        Assert.Equal("Renamed", updated.Name);
        Assert.Equal(new[] { "Recruiting" }, updated.Objectives);
        Assert.Null(updated.Notes);
        Assert.Equal(new SponsorshipOwnBudgetResponse(null, null, false), updated.Budget);

        var delete = await client.DeleteAsync($"/api/sponsorship/goals/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        var after = await client.GetAsync($"/api/sponsorship/goals/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, after.StatusCode);
        Assert.Equal("sponsorship_goal_not_found", await ErrorCodeAsync(after));
    }

    [Fact]
    public async Task Update_KeepsStatus_AndCompanyCanHoldSeveralSets_NewestFirst()
    {
        using var client = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        var first = await CreateAsync(client, ValidBody("First"));
        await Task.Delay(10);
        var second = await CreateAsync(client, ValidBody("Second"));
        await SetStatusAsync(client, first.Id, "Paused");

        var edited = (await (await client.PutAsJsonAsync($"/api/sponsorship/goals/{first.Id}", ValidBody("First edited")))
            .Content.ReadFromJsonAsync<SponsorshipGoalSetResponse>(JsonOptions))!;
        Assert.Equal("Paused", edited.Status);

        var list = (await client.GetFromJsonAsync<SponsorshipGoalSetListResponse>("/api/sponsorship/goals", JsonOptions))!;
        Assert.Equal(new[] { second.Id, first.Id }, list.Items.Select(i => i.Id).ToArray());
    }

    [Theory]
    [InlineData("name", "A", "sponsorship_goal_name_invalid")]
    [InlineData("name", null, "sponsorship_goal_name_invalid")]
    [InlineData("name", "TOO_LONG_NAME", "sponsorship_goal_name_invalid")]
    [InlineData("companyName", "A", "sponsorship_goal_company_name_invalid")]
    [InlineData("companyName", "TOO_LONG_COMPANY", "sponsorship_goal_company_name_invalid")]
    [InlineData("notes", "TOO_LONG_NOTES", "sponsorship_goal_notes_invalid")]
    public async Task Create_InvalidScalar_Returns400WithErrorCode(string field, string? value, string expectedCode)
    {
        using var client = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        value = value switch
        {
            "TOO_LONG_NAME" => new string('n', 101),
            "TOO_LONG_COMPANY" => new string('c', 151),
            "TOO_LONG_NOTES" => new string('x', 1001),
            _ => value,
        };
        var body = ValidBody("Valid");
        body[field] = value;
        var response = await client.PostAsJsonAsync("/api/sponsorship/goals", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, await ErrorCodeAsync(response));
    }

    [Theory]
    [InlineData("objectives_empty", "sponsorship_goal_objectives_invalid")]
    [InlineData("objectives_unknown", "sponsorship_goal_objectives_invalid")]
    [InlineData("objectives_null", "sponsorship_goal_objectives_invalid")]
    [InlineData("event_kinds_empty", "sponsorship_goal_event_kinds_invalid")]
    [InlineData("event_kinds_unknown", "sponsorship_goal_event_kinds_invalid")]
    [InlineData("fields_count", "sponsorship_goal_fields_of_study_count_invalid")]
    [InlineData("field_short", "sponsorship_goal_field_of_study_invalid")]
    [InlineData("field_long", "sponsorship_goal_field_of_study_invalid")]
    [InlineData("cities_count", "sponsorship_goal_cities_count_invalid")]
    [InlineData("city_short", "sponsorship_goal_city_invalid")]
    [InlineData("universities_count", "sponsorship_goal_universities_count_invalid")]
    [InlineData("university_long", "sponsorship_goal_university_invalid")]
    [InlineData("year_zero", "sponsorship_goal_years_invalid")]
    [InlineData("year_seven", "sponsorship_goal_years_invalid")]
    [InlineData("audience_empty", "sponsorship_goal_audience_required")]
    [InlineData("audience_null", "sponsorship_goal_audience_required")]
    [InlineData("budget_negative", "sponsorship_goal_budget_invalid")]
    [InlineData("budget_too_big", "sponsorship_goal_budget_invalid")]
    [InlineData("budget_min_over_max", "sponsorship_goal_budget_invalid")]
    public async Task Create_InvalidCollections_Returns400WithErrorCode(string scenario, string expectedCode)
    {
        using var client = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        var body = ValidBody("Valid");
        var audience = new Dictionary<string, object?>
        {
            ["fieldsOfStudy"] = new[] { "Computer Science" },
            ["years"] = new[] { 1 },
            ["cities"] = Array.Empty<string>(),
            ["universities"] = Array.Empty<string>(),
        };
        string[] Many(int n, string prefix) => Enumerable.Range(0, n).Select(i => $"{prefix} {i}").ToArray();
        switch (scenario)
        {
            case "objectives_empty": body["objectives"] = Array.Empty<string>(); break;
            case "objectives_unknown": body["objectives"] = new[] { "World domination" }; break;
            case "objectives_null": body["objectives"] = null; break;
            case "event_kinds_empty": body["eventKinds"] = Array.Empty<string>(); break;
            case "event_kinds_unknown": body["eventKinds"] = new[] { "Rave" }; break;
            case "fields_count": audience["fieldsOfStudy"] = Many(16, "Field"); break;
            case "field_short": audience["fieldsOfStudy"] = new[] { "X" }; break;
            case "field_long": audience["fieldsOfStudy"] = new[] { new string('f', 61) }; break;
            case "cities_count": audience["cities"] = Many(11, "City"); break;
            case "city_short": audience["cities"] = new[] { "X" }; break;
            case "universities_count": audience["universities"] = Many(11, "University"); break;
            case "university_long": audience["universities"] = new[] { new string('u', 151) }; break;
            case "year_zero": audience["years"] = new[] { 0 }; break;
            case "year_seven": audience["years"] = new[] { 7 }; break;
            case "audience_empty":
                audience["fieldsOfStudy"] = Array.Empty<string>();
                audience["years"] = Array.Empty<int>();
                break;
            case "audience_null": body["audience"] = null; break;
            case "budget_negative": body["budget"] = new { min = -1, max = 10, visibleToClubs = true }; break;
            case "budget_too_big": body["budget"] = new { min = 0, max = 1_000_000_001, visibleToClubs = true }; break;
            case "budget_min_over_max": body["budget"] = new { min = 500, max = 100, visibleToClubs = true }; break;
        }

        if (scenario != "audience_null")
        {
            body["audience"] = audience;
        }

        var response = await client.PostAsJsonAsync("/api/sponsorship/goals", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Create_AtTheLimits_Succeeds()
    {
        using var client = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        var body = ValidBody(new string('n', 100));
        body["companyName"] = new string('c', 150);
        body["objectives"] = new[] { "Brand awareness", "Recruiting", "CSR education", "Community outreach", "Product launch", "Other" };
        body["eventKinds"] = new[] { "Hackathon", "Competition", "Workshop", "Career fair", "Conference", "Cultural event", "Sports event", "Seminar" };
        body["audience"] = new
        {
            fieldsOfStudy = Enumerable.Range(0, 15).Select(i => $"Field {i}").ToArray(),
            years = new[] { 6, 1 },
            cities = Enumerable.Range(0, 10).Select(i => $"City {i}").ToArray(),
            universities = Enumerable.Range(0, 10).Select(i => $"University {i}").ToArray(),
        };
        body["budget"] = new { min = 0, max = 1_000_000_000, visibleToClubs = true };
        body["notes"] = new string('x', 1000);
        var created = await CreateAsync(client, body);
        Assert.Equal(new[] { 1, 6 }, created.Audience.Years);
        Assert.Equal(6, created.Objectives.Count);
        Assert.Equal(8, created.EventKinds.Count);
    }

    [Fact]
    public async Task Create_AudienceWithOnlyYears_Cities_OrUniversities_Succeeds()
    {
        using var client = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        foreach (var audience in new object[]
        {
            new { fieldsOfStudy = Array.Empty<string>(), years = new[] { 2 }, cities = Array.Empty<string>(), universities = Array.Empty<string>() },
            new { fieldsOfStudy = Array.Empty<string>(), years = Array.Empty<int>(), cities = new[] { "Sylhet" }, universities = Array.Empty<string>() },
            new { fieldsOfStudy = Array.Empty<string>(), years = Array.Empty<int>(), cities = Array.Empty<string>(), universities = new[] { "NSU" } },
        })
        {
            var body = ValidBody("Audience");
            body["audience"] = audience;
            await CreateAsync(client, body);
        }
    }

    [Fact]
    public async Task Status_SetsPausedAndActive_SameStatusIsNoOpWithoutAudit_InvalidStatusIs400()
    {
        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();
        using var client = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        var created = await CreateAsync(client, ValidBody("Toggle"));

        Assert.Equal("Active", (await SetStatusAsync(client, created.Id, "Active")).Status);
        var paused = await SetStatusAsync(client, created.Id, "Paused");
        Assert.Equal("Paused", paused.Status);
        Assert.Equal("Paused", (await SetStatusAsync(client, created.Id, "Paused")).Status);
        Assert.Equal("Active", (await SetStatusAsync(client, created.Id, "Active")).Status);

        var invalid = await client.PostAsJsonAsync($"/api/sponsorship/goals/{created.Id}/status", new { status = "Archived" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("sponsorship_goal_status_invalid", await ErrorCodeAsync(invalid));

        var rows = audit.Recorded.Where(r => r.ResourceId == created.Id.ToString()).ToList();
        Assert.Equal(
            new[] { "sponsorship_goal_created", "sponsorship_goal_status_changed", "sponsorship_goal_status_changed" },
            rows.Select(r => r.Action).ToArray());
    }

    [Fact]
    public async Task Audit_RowsContainOnlyIdsAndStatus()
    {
        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();
        var owner = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(owner);
        var body = ValidBody("Secret Campaign Name");
        body["notes"] = "Confidential note text";
        var created = await CreateAsync(client, body);
        await client.PutAsJsonAsync($"/api/sponsorship/goals/{created.Id}", body);
        await SetStatusAsync(client, created.Id, "Paused");
        await client.DeleteAsync($"/api/sponsorship/goals/{created.Id}");

        var rows = audit.Recorded.Where(r => r.ResourceId == created.Id.ToString()).ToList();
        Assert.Equal(
            new[] { "sponsorship_goal_created", "sponsorship_goal_updated", "sponsorship_goal_status_changed", "sponsorship_goal_deleted" },
            rows.Select(r => r.Action).ToArray());
        foreach (var row in rows)
        {
            Assert.Equal("SponsorshipGoalSet", row.ResourceType);
            using var meta = JsonDocument.Parse(row.MetadataJson!);
            var props = meta.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
            Assert.Equal(created.Id.ToString(), props["goalId"]);
            Assert.True(props.Count == (row.Action == "sponsorship_goal_status_changed" ? 2 : 1));
            Assert.DoesNotContain("Secret", row.MetadataJson, StringComparison.Ordinal);
            Assert.DoesNotContain("Confidential", row.MetadataJson, StringComparison.Ordinal);
            Assert.DoesNotContain(owner.Email, row.MetadataJson, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal("Paused", JsonDocument.Parse(rows[2].MetadataJson!).RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CrossCompany_ForeignIdsReturn404_AndListShowsOnlyOwnSets()
    {
        using var owner = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        using var other = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        var mine = await CreateAsync(owner, ValidBody("Mine"));
        var theirs = await CreateAsync(other, ValidBody("Theirs"));

        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/sponsorship/goals/{mine.Id}")).StatusCode);
        var put = await other.PutAsJsonAsync($"/api/sponsorship/goals/{mine.Id}", ValidBody("Hijack"));
        Assert.Equal(HttpStatusCode.NotFound, put.StatusCode);
        Assert.Equal("sponsorship_goal_not_found", await ErrorCodeAsync(put));
        var status = await other.PostAsJsonAsync($"/api/sponsorship/goals/{mine.Id}/status", new { status = "Paused" });
        Assert.Equal(HttpStatusCode.NotFound, status.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/sponsorship/goals/{mine.Id}")).StatusCode);

        var unchanged = (await owner.GetFromJsonAsync<SponsorshipGoalSetResponse>($"/api/sponsorship/goals/{mine.Id}", JsonOptions))!;
        Assert.Equal("Mine", unchanged.Name);
        Assert.Equal("Active", unchanged.Status);

        var list = (await owner.GetFromJsonAsync<SponsorshipGoalSetListResponse>("/api/sponsorship/goals", JsonOptions))!;
        Assert.Equal(new[] { mine.Id }, list.Items.Select(i => i.Id).ToArray());
        Assert.DoesNotContain(list.Items, i => i.Id == theirs.Id);
    }

    [Fact]
    public async Task ClubList_ShowsActiveOnly_HidesPausedAndDeleted_WithSummaryShape()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var org = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        using var club = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));

        var active = await CreateAsync(org, ValidBody($"Visible {suffix}"));
        var paused = await CreateAsync(org, ValidBody($"Hidden {suffix}"));
        var deleted = await CreateAsync(org, ValidBody($"Gone {suffix}"));
        await SetStatusAsync(org, paused.Id, "Paused");
        await org.DeleteAsync($"/api/sponsorship/goals/{deleted.Id}");

        var list = (await club.GetFromJsonAsync<CompanyGoalListResponse>($"/api/sponsorship/companies?q={suffix}", JsonOptions))!;
        var item = Assert.Single(list.Items);
        Assert.Equal(active.Id, item.Id);
        Assert.Equal("Acme Ltd", item.CompanyName);
        Assert.Equal(new[] { "Brand awareness" }, item.Objectives);
        Assert.Equal(new[] { "Hackathon" }, item.EventKinds);
        Assert.Equal(new[] { "Computer Science", "Engineering" }, item.FieldsOfStudy);

        using var doc = JsonDocument.Parse(await club.GetStringAsync($"/api/sponsorship/companies?q={suffix}"));
        var first = doc.RootElement.GetProperty("items")[0];
        Assert.Equal(
            new[] { "budget", "companyName", "eventKinds", "fieldsOfStudy", "id", "name", "objectives" },
            first.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task ClubList_NewestFirst_AndFiltersByObjectiveEventKindAndQuery()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var orgA = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        using var orgB = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        using var club = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));

        var a = ValidBody($"Alpha {suffix}");
        a["companyName"] = $"Zeta Corp {suffix}";
        a["objectives"] = new[] { "Recruiting", "Product launch" };
        a["eventKinds"] = new[] { "Career fair" };
        var setA = await CreateAsync(orgA, a);
        await Task.Delay(10);
        var b = ValidBody($"Beta {suffix}");
        b["companyName"] = $"Yankee Ltd {suffix}";
        b["objectives"] = new[] { "CSR education" };
        b["eventKinds"] = new[] { "Workshop", "Seminar" };
        var setB = await CreateAsync(orgB, b);

        async Task<Guid[]> Ids(string query) =>
            (await club.GetFromJsonAsync<CompanyGoalListResponse>($"/api/sponsorship/companies?{query}", JsonOptions))!
                .Items.Select(i => i.Id).ToArray();

        Assert.Equal(new[] { setB.Id, setA.Id }, await Ids($"q={suffix}"));
        var byObjectiveText = (await club.GetFromJsonAsync<CompanyGoalListResponse>("/api/sponsorship/companies?q=product%20launch", JsonOptions))!;
        Assert.Contains(byObjectiveText.Items, i => i.Id == setA.Id);
        Assert.DoesNotContain(byObjectiveText.Items, i => i.Id == setB.Id);
        Assert.Equal(new[] { setA.Id }, await Ids($"q={suffix}&objective=recruiting"));
        Assert.Equal(new[] { setA.Id }, await Ids($"q={suffix}&objective=%20PRODUCT%20LAUNCH%20"));
        Assert.Equal(new[] { setB.Id }, await Ids($"q={suffix}&objective=CSR%20education"));
        Assert.Empty(await Ids($"q={suffix}&objective=CSR"));
        Assert.Equal(new[] { setB.Id }, await Ids($"q={suffix}&eventKind=seminar"));
        Assert.Equal(new[] { setA.Id }, await Ids($"q={suffix}&eventKind=Career%20fair"));
        Assert.Empty(await Ids($"q={suffix}&objective=Recruiting&eventKind=Workshop"));
        Assert.Equal(new[] { setA.Id }, await Ids($"q=zeta%20corp%20{suffix}"));
        Assert.Equal(new[] { setB.Id }, await Ids($"q=BETA%20{suffix}"));
        // q also matches objective text (other tests share the database, so keep the suffix to isolate).
        Assert.Equal(new[] { setB.Id }, await Ids($"q=Yankee%20Ltd%20{suffix}&objective=CSR%20education"));
    }

    [Fact]
    public async Task ClubDetail_ReturnsActive_404ForPausedAndUnknown()
    {
        using var org = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        using var club = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        var body = ValidBody("Detail");
        body["notes"] = "Reach out early";
        var created = await CreateAsync(org, body);

        var detail = (await club.GetFromJsonAsync<CompanyGoalDetail>($"/api/sponsorship/companies/{created.Id}", JsonOptions))!;
        Assert.Equal(created.Id, detail.Id);
        Assert.Equal("Acme Ltd", detail.CompanyName);
        Assert.Equal(new[] { 1, 2, 3 }, detail.Audience.Years);
        Assert.Equal(new[] { "Dhaka" }, detail.Audience.Cities);
        Assert.Equal(new[] { "BUET" }, detail.Audience.Universities);
        Assert.Equal("Reach out early", detail.Notes);

        await SetStatusAsync(org, created.Id, "Paused");
        var paused = await club.GetAsync($"/api/sponsorship/companies/{created.Id}");
        Assert.Equal(HttpStatusCode.NotFound, paused.StatusCode);
        Assert.Equal("sponsorship_goal_not_found", await ErrorCodeAsync(paused));

        var unknown = await club.GetAsync($"/api/sponsorship/companies/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("sponsorship_goal_not_found", await ErrorCodeAsync(unknown));
    }

    [Fact]
    public async Task Budget_HiddenFromClubsUnlessVisible_AndAtLeastOneBoundExists()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var org = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        using var club = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));

        async Task<SponsorshipGoalSetResponse> Make(string name, object budget)
        {
            var body = ValidBody($"{name} {suffix}");
            body["budget"] = budget;
            return await CreateAsync(org, body);
        }

        var hidden = await Make("Hidden", new { min = 1000, max = 5000, visibleToClubs = false });
        var visible = await Make("Visible", new { min = 1000, max = 5000, visibleToClubs = true });
        var minOnly = await Make("MinOnly", new { min = 2000, max = (int?)null, visibleToClubs = true });
        var noBounds = await Make("NoBounds", new { min = (int?)null, max = (int?)null, visibleToClubs = true });

        var list = (await club.GetFromJsonAsync<CompanyGoalListResponse>($"/api/sponsorship/companies?q={suffix}", JsonOptions))!;
        SponsorshipPublicBudgetResponse? BudgetOf(Guid id) => list.Items.Single(i => i.Id == id).Budget;
        Assert.Null(BudgetOf(hidden.Id));
        Assert.Equal(new SponsorshipPublicBudgetResponse(1000, 5000), BudgetOf(visible.Id));
        Assert.Equal(new SponsorshipPublicBudgetResponse(2000, null), BudgetOf(minOnly.Id));
        Assert.Null(BudgetOf(noBounds.Id));

        var hiddenDetail = await club.GetStringAsync($"/api/sponsorship/companies/{hidden.Id}");
        using (var doc = JsonDocument.Parse(hiddenDetail))
        {
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("budget").ValueKind);
        }

        Assert.DoesNotContain("1000", hiddenDetail, StringComparison.Ordinal);
        Assert.DoesNotContain("5000", hiddenDetail, StringComparison.Ordinal);
        var visibleDetail = (await club.GetFromJsonAsync<CompanyGoalDetail>($"/api/sponsorship/companies/{visible.Id}", JsonOptions))!;
        Assert.Equal(new SponsorshipPublicBudgetResponse(1000, 5000), visibleDetail.Budget);
        var noBoundsDetail = (await club.GetFromJsonAsync<CompanyGoalDetail>($"/api/sponsorship/companies/{noBounds.Id}", JsonOptions))!;
        Assert.Null(noBoundsDetail.Budget);
    }

    [Fact]
    public async Task Authorization_CompanyEndpointsRejectClubStudentAnonymous_ClubEndpointsRejectOrgStudentAnonymous()
    {
        var id = Guid.NewGuid();
        using var student = await ClientForAsync(await SeedUserAsync(ActorTypes.Student));
        using var org = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        using var club = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        using var anonymous = _factory.CreateClient();

        foreach (var client in new[] { student, club })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/sponsorship/goals", ValidBody("x"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/sponsorship/goals")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/sponsorship/goals/{id}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync($"/api/sponsorship/goals/{id}", ValidBody("x"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/sponsorship/goals/{id}/status", new { status = "Paused" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/sponsorship/goals/{id}")).StatusCode);
        }

        foreach (var client in new[] { student, org })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/sponsorship/companies")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/sponsorship/companies/{id}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/sponsorship/goals", ValidBody("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/sponsorship/goals")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/sponsorship/goals/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync($"/api/sponsorship/goals/{id}", ValidBody("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync($"/api/sponsorship/goals/{id}/status", new { status = "Paused" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.DeleteAsync($"/api/sponsorship/goals/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/sponsorship/companies")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/sponsorship/companies/{id}")).StatusCode);
    }

    [Fact]
    public async Task ClubResponses_NeverContainOwnerAccountIdOrEmail()
    {
        var owner = await SeedUserAsync(ActorTypes.Organization);
        using var org = await ClientForAsync(owner);
        using var club = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var created = await CreateAsync(org, ValidBody($"Leak Check {suffix}"));

        var list = await club.GetAsync($"/api/sponsorship/companies?q={suffix}");
        var single = await club.GetAsync($"/api/sponsorship/companies/{created.Id}");
        foreach (var response in new[] { list, single })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(owner.Id.ToString(), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(owner.Email, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ownerAccountId", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("email", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Persistence_RowIsNonTenantAndStoredWithOwner()
    {
        var owner = await SeedUserAsync(ActorTypes.Organization);
        using var org = await ClientForAsync(owner);
        var created = await CreateAsync(org, ValidBody("Stored"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var row = await db.SponsorshipGoalSets.AsNoTracking().SingleAsync(s => s.Id == created.Id);
        Assert.Equal(owner.Id, row.OwnerAccountId);
        Assert.Equal("Active", row.Status);
        Assert.Equal("[1,2,3]", row.AudienceYears);
    }

    private static Dictionary<string, object?> ValidBody(string name) => new()
    {
        ["name"] = name,
        ["companyName"] = "Acme Ltd",
        ["objectives"] = new[] { "Brand awareness" },
        ["audience"] = new
        {
            fieldsOfStudy = new[] { "Computer Science", "Engineering", "computer science" },
            years = new[] { 3, 1, 2, 2 },
            cities = new[] { "Dhaka" },
            universities = new[] { "BUET" },
        },
        ["eventKinds"] = new[] { "Hackathon" },
        ["budget"] = new { min = (int?)null, max = (int?)null, visibleToClubs = false },
        ["notes"] = null,
    };

    private static async Task<SponsorshipGoalSetResponse> CreateAsync(HttpClient client, Dictionary<string, object?> body)
    {
        var response = await client.PostAsJsonAsync("/api/sponsorship/goals", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SponsorshipGoalSetResponse>(JsonOptions))!;
    }

    private static async Task<SponsorshipGoalSetResponse> SetStatusAsync(HttpClient client, Guid id, string status)
    {
        var response = await client.PostAsJsonAsync($"/api/sponsorship/goals/{id}/status", new { status });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SponsorshipGoalSetResponse>(JsonOptions))!;
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
        var tokens = await tokenService.IssueTokensAsync(user, "sponsorship-goal-test", CancellationToken.None);
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
