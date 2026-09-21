using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.InstitutionalClubNetwork.ClubProfiles;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Auth;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.InstitutionalClubNetwork;

/// <summary>
/// STOR-69 end-to-end coverage of the club profile endpoints (<c>/api/clubs/profile</c>) and the
/// company browse endpoints (<c>/api/clubs</c>) through the real pipeline with real-issued tokens.
/// </summary>
public class ClubProfileEndpointsTests : IClassFixture<DiscoveryHiringEndpointsFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DiscoveryHiringEndpointsFactory _factory;

    public ClubProfileEndpointsTests(DiscoveryHiringEndpointsFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Get_BeforeAnySave_Returns404WithCode()
    {
        using var client = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        var response = await client.GetAsync("/api/clubs/profile");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("club_profile_not_found", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Put_CreatesDraft_ThenGetReturnsSameShape()
    {
        using var client = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        var eventId = Guid.NewGuid();
        var body = ValidBody("  Robotics Club  ");
        body["tagline"] = "Build things";
        body["city"] = " Dhaka ";
        body["foundedYear"] = 2015;
        body["events"] = new object[]
        {
            new { id = eventId, title = "Robo Wars", description = "  Annual contest ", typicalAttendance = 250, frequency = "Yearly", supportNeeds = new[] { "Prizes", "Venue", "Prizes" } },
            new { title = "Workshop", description = (string?)null, typicalAttendance = 40, frequency = "Monthly", supportNeeds = Array.Empty<string>() },
        };

        var put = await client.PutAsJsonAsync("/api/clubs/profile", body);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var created = (await put.Content.ReadFromJsonAsync<ClubProfileResponse>(JsonOptions))!;
        Assert.Equal("Draft", created.Status);
        Assert.Null(created.PublishedAt);
        Assert.Equal("Robotics Club", created.Name);
        Assert.Equal("Build things", created.Tagline);
        Assert.Equal("Dhaka", created.City);
        Assert.Equal(2015, created.FoundedYear);
        Assert.Equal(new[] { "Computer Science", "Engineering" }, created.Audience.FieldsOfStudy);
        Assert.Equal(new[] { 1, 2, 3 }, created.Audience.Years);
        Assert.Equal(2, created.Events.Count);
        Assert.Equal(eventId, created.Events[0].Id);
        Assert.Equal("Annual contest", created.Events[0].Description);
        Assert.Equal(new[] { "Venue", "Prizes" }, created.Events[0].SupportNeeds);
        Assert.NotEqual(Guid.Empty, created.Events[1].Id);
        Assert.Null(created.Events[1].Description);

        var get = (await client.GetFromJsonAsync<ClubProfileResponse>("/api/clubs/profile", JsonOptions))!;
        Assert.Equal(created.Id, get.Id);
        Assert.Equal(created.Events.Select(e => e.Id), get.Events.Select(e => e.Id));

        // Wire shape is camelCase with string frequency.
        using var doc = JsonDocument.Parse(await client.GetStringAsync("/api/clubs/profile"));
        Assert.Equal("Yearly", doc.RootElement.GetProperty("events")[0].GetProperty("frequency").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("audience").GetProperty("years").GetArrayLength());
    }

    [Fact]
    public async Task Put_Twice_UpdatesSameProfile_AndKeepsStatus()
    {
        using var client = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        var first = await SaveAsync(client, ValidBody("First Name"));
        var publish = await client.PostAsync("/api/clubs/profile/publish", null);
        Assert.Equal(HttpStatusCode.OK, publish.StatusCode);

        var edited = await SaveAsync(client, ValidBody("Second Name"));
        Assert.Equal(first.Id, edited.Id);
        Assert.Equal("Second Name", edited.Name);
        Assert.Equal("Published", edited.Status);
        Assert.NotNull(edited.PublishedAt);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        Assert.Equal(1, await db.ClubProfiles.CountAsync(p => p.Id == first.Id));
    }

    [Theory]
    [InlineData("name", "A", "club_profile_name_invalid")]
    [InlineData("tagline", "TOO_LONG_TAGLINE", "club_profile_tagline_invalid")]
    [InlineData("about", "too short", "club_profile_about_invalid")]
    [InlineData("university", "U", "club_profile_university_invalid")]
    [InlineData("city", "TOO_LONG_CITY", "club_profile_city_invalid")]
    [InlineData("foundedYear", 1899, "club_profile_founded_year_invalid")]
    [InlineData("foundedYear", 9999, "club_profile_founded_year_invalid")]
    [InlineData("memberCount", -1, "club_profile_member_count_invalid")]
    [InlineData("memberCount", 1_000_001, "club_profile_member_count_invalid")]
    [InlineData("memberCount", null, "club_profile_member_count_invalid")]
    public async Task Put_InvalidScalar_Returns400WithErrorCode(string field, object? value, string expectedCode)
    {
        using var client = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        if (value is string s && s == "TOO_LONG_TAGLINE")
        {
            value = new string('t', 201);
        }
        else if (value is string c && c == "TOO_LONG_CITY")
        {
            value = new string('c', 101);
        }

        var body = ValidBody("Valid Club");
        body[field] = value;
        var response = await client.PutAsJsonAsync("/api/clubs/profile", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Put_ZeroMembersAndFoundedThisYear_AreAllowed()
    {
        using var client = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        var body = ValidBody("Brand New Club");
        body["memberCount"] = 0;
        body["foundedYear"] = DateTime.UtcNow.Year;
        var response = await client.PutAsJsonAsync("/api/clubs/profile", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("fields_count", "club_profile_fields_of_study_count_invalid")]
    [InlineData("field_short", "club_profile_field_of_study_invalid")]
    [InlineData("field_long", "club_profile_field_of_study_invalid")]
    [InlineData("year_zero", "club_profile_years_invalid")]
    [InlineData("year_seven", "club_profile_years_invalid")]
    [InlineData("events_count", "club_profile_events_count_invalid")]
    [InlineData("event_title", "club_profile_event_title_invalid")]
    [InlineData("event_description", "club_profile_event_description_invalid")]
    [InlineData("event_attendance_zero", "club_profile_event_attendance_invalid")]
    [InlineData("event_attendance_big", "club_profile_event_attendance_invalid")]
    [InlineData("event_frequency", "club_profile_event_frequency_invalid")]
    [InlineData("event_support", "club_profile_event_support_need_invalid")]
    public async Task Put_InvalidCollections_Return400WithErrorCode(string scenario, string expectedCode)
    {
        using var client = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        var body = ValidBody("Valid Club");
        switch (scenario)
        {
            case "fields_count":
                body["audience"] = new { fieldsOfStudy = Enumerable.Range(0, 16).Select(i => $"Field {i:00}").ToArray(), years = new[] { 1 } };
                break;
            case "field_short":
                body["audience"] = new { fieldsOfStudy = new[] { "X" }, years = new[] { 1 } };
                break;
            case "field_long":
                body["audience"] = new { fieldsOfStudy = new[] { new string('f', 61) }, years = new[] { 1 } };
                break;
            case "year_zero":
                body["audience"] = new { fieldsOfStudy = new[] { "Law" }, years = new[] { 0 } };
                break;
            case "year_seven":
                body["audience"] = new { fieldsOfStudy = new[] { "Law" }, years = new[] { 7 } };
                break;
            case "events_count":
                body["events"] = Enumerable.Range(0, 13).Select(i => (object)EventBody($"Event {i}")).ToArray();
                break;
            case "event_title":
                body["events"] = new object[] { EventBody("ab") };
                break;
            case "event_description":
                body["events"] = new object[] { EventBody("Good title", description: new string('d', 1001)) };
                break;
            case "event_attendance_zero":
                body["events"] = new object[] { EventBody("Good title", attendance: 0) };
                break;
            case "event_attendance_big":
                body["events"] = new object[] { EventBody("Good title", attendance: 100_001) };
                break;
            case "event_frequency":
                body["events"] = new object[] { EventBody("Good title", frequency: "Weekly") };
                break;
            case "event_support":
                body["events"] = new object[] { EventBody("Good title", support: new[] { "Yachts" }) };
                break;
        }

        var response = await client.PutAsJsonAsync("/api/clubs/profile", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Put_DeduplicatesFieldsCaseInsensitively_AndSortsYears()
    {
        using var client = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        var body = ValidBody("Dedupe Club");
        body["audience"] = new { fieldsOfStudy = new[] { " Law ", "law", "Business", "BUSINESS " }, years = new[] { 4, 2, 4, 1 } };
        var saved = await SaveAsync(client, body);
        Assert.Equal(new[] { "Law", "Business" }, saved.Audience.FieldsOfStudy);
        Assert.Equal(new[] { 1, 2, 4 }, saved.Audience.Years);
    }

    [Fact]
    public async Task Publish_WithoutProfile_Returns404_AndIncompleteReturns400NamingWhatIsMissing()
    {
        using var client = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        var none = await client.PostAsync("/api/clubs/profile/publish", null);
        Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        Assert.Equal("club_profile_not_found", await ErrorCodeAsync(none));

        var body = ValidBody("Incomplete Club");
        body["audience"] = new { fieldsOfStudy = Array.Empty<string>(), years = Array.Empty<int>() };
        await SaveAsync(client, body);

        var response = await client.PostAsync("/api/clubs/profile/publish", null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("club_profile_incomplete", await ErrorCodeAsync(response));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var message = doc.RootElement.GetProperty("message").GetString()!;
        Assert.Contains("field of study", message, StringComparison.Ordinal);
        Assert.Contains("study year", message, StringComparison.Ordinal);
        Assert.DoesNotContain("club name", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Publish_BrandNewClubWithZeroMembersAndNoEvents_Succeeds_ThenIsIdempotent_ThenUnpublishes()
    {
        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();
        using var client = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        var body = ValidBody("Fresh Club");
        body["memberCount"] = 0;
        body["events"] = Array.Empty<object>();
        var saved = await SaveAsync(client, body);

        var published = await PostProfileActionAsync(client, "publish");
        Assert.Equal("Published", published.Status);
        Assert.NotNull(published.PublishedAt);
        var again = await PostProfileActionAsync(client, "publish");
        Assert.Equal(published.PublishedAt, again.PublishedAt);

        var unpublished = await PostProfileActionAsync(client, "unpublish");
        Assert.Equal("Draft", unpublished.Status);
        Assert.Null(unpublished.PublishedAt);
        var draftAgain = await PostProfileActionAsync(client, "unpublish");
        Assert.Equal("Draft", draftAgain.Status);

        var rows = audit.Recorded.Where(r => r.ResourceId == saved.Id.ToString()).ToList();
        Assert.Equal(
            new[] { "club_profile_updated", "club_profile_published", "club_profile_unpublished" },
            rows.Select(r => r.Action).ToArray());
        foreach (var row in rows)
        {
            Assert.Equal("ClubProfile", row.ResourceType);
            using var meta = JsonDocument.Parse(row.MetadataJson!);
            var props = meta.RootElement.EnumerateObject().ToList();
            Assert.Single(props);
            Assert.Equal("clubProfileId", props[0].Name);
            Assert.Equal(saved.Id.ToString(), props[0].Value.GetString());
        }
    }

    [Fact]
    public async Task Browse_ListsPublishedOnly_NewestFirst_WithSummaryShape()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var clubA = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        using var clubB = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        using var clubDraft = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        using var org = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));

        var bodyA = ValidBody($"Alpha {suffix}");
        bodyA["events"] = new object[] { EventBody("One"), EventBody("Two") };
        var a = await SaveAsync(clubA, bodyA);
        await SaveAsync(clubB, ValidBody($"Beta {suffix}"));
        await SaveAsync(clubDraft, ValidBody($"Gamma {suffix}"));
        await PostProfileActionAsync(clubA, "publish");
        await Task.Delay(10);
        var b = await clubB.PostAsync("/api/clubs/profile/publish", null);
        Assert.Equal(HttpStatusCode.OK, b.StatusCode);

        var list = (await org.GetFromJsonAsync<ClubListResponse>($"/api/clubs?q={suffix}", JsonOptions))!;
        Assert.Equal(new[] { $"Beta {suffix}", $"Alpha {suffix}" }, list.Items.Select(i => i.Name).ToArray());
        var alpha = list.Items.Single(i => i.Id == a.Id);
        Assert.Equal(2, alpha.EventCount);
        Assert.Equal(new[] { "Computer Science", "Engineering" }, alpha.FieldsOfStudy);
        Assert.Equal("Dhaka University", alpha.University);
        Assert.Equal(120, alpha.MemberCount);

        // STOR-69 redo Phase 1: the summary now also carries FoundedYear,
        // AudienceYears (sorted/deduped, same as the detail response),
        // EventAttendanceSummary, and SupportNeeds (deduped/sorted across events).
        // ValidBody leaves FoundedYear null and the default AudienceYears [1,2,3]
        // and no events for the gamma/betac lubs; Alpha has two default events so
        // its attendance range collapses to {50,50} and its support needs collapse
        // to the single value {"Funding"} (the EventBody helper's default).
        Assert.Null(alpha.FoundedYear);
        Assert.Equal(new[] { 1, 2, 3 }, alpha.AudienceYears);
        Assert.Equal(new ClubEventAttendanceSummary(50, 50), alpha.EventAttendanceSummary);
        Assert.Equal(new[] { "Funding" }, alpha.SupportNeeds);

        // The list response now also carries a Total count of all matching rows.
        Assert.Equal(2, list.Total);

        using var doc = JsonDocument.Parse(await org.GetStringAsync($"/api/clubs?q={suffix}"));
        var names = doc.RootElement.GetProperty("items")[0].EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray();
        Assert.Equal(
            new[] { "audienceYears", "eventAttendanceSummary", "eventCount", "fieldsOfStudy", "foundedYear", "id", "memberCount", "name", "supportNeeds", "tagline", "university" },
            names);
        Assert.Equal(2, doc.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Browse_Filters_QueryFieldAndUniversity_AreCaseInsensitive()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var club1 = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        using var club2 = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        using var org = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));

        var b1 = ValidBody($"Chess {suffix}");
        b1["tagline"] = $"Think ahead {suffix}";
        b1["university"] = $"North South {suffix}";
        b1["audience"] = new { fieldsOfStudy = new[] { "Mathematics" }, years = new[] { 1 } };
        var b2 = ValidBody($"Film {suffix}");
        b2["university"] = $"BRAC {suffix}";
        b2["audience"] = new { fieldsOfStudy = new[] { "Media Studies", "Mathematics Extra" }, years = new[] { 2 } };
        var p1 = await SaveAsync(club1, b1);
        var p2 = await SaveAsync(club2, b2);
        await PostProfileActionAsync(club1, "publish");
        await PostProfileActionAsync(club2, "publish");

        async Task<Guid[]> Ids(string query) =>
            (await org.GetFromJsonAsync<ClubListResponse>($"/api/clubs?{query}", JsonOptions))!
                .Items.Select(i => i.Id).ToArray();

        Assert.Equal(new[] { p1.Id }, await Ids($"q={suffix}&field=%20mathematics%20"));
        Assert.Equal(new[] { p2.Id }, await Ids($"q={suffix}&field=MEDIA%20STUDIES"));
        Assert.Equal(new[] { p1.Id }, await Ids($"q=think%20AHEAD"));
        Assert.Equal(new[] { p1.Id }, await Ids($"university=north%20south%20{suffix}"));
        Assert.Equal(new[] { p2.Id }, await Ids($"q={suffix}&university=brac%20{suffix}"));
        Assert.Empty(await Ids($"q={suffix}&field=Math")); // exact match, not substring
        Assert.Empty(await Ids($"q=does-not-exist-{suffix}"));
    }

    [Fact]
    public async Task Browse_WithMoreThanMaxResults_ReturnsTruncatedItemsAndFullTotal()
    {
        // STOR-69 redo Phase 1: BrowseClubsHandler returns at most MaxResults (50) items
        // but surfaces the unfiltered count as Total so the caller can render a
        // "showing N of total" affordance. Seed 55 Published clubs with a unique
        // query suffix so the assertion is contained.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var org = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));

        const int seeded = 55;
        var ids = new List<Guid>(seeded);
        for (var i = 0; i < seeded; i++)
        {
            using var clubClient = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
            var body = ValidBody($"Truncation {suffix} #{i:00}");
            var saved = await SaveAsync(clubClient, body);
            await PostProfileActionAsync(clubClient, "publish");
            ids.Add(saved.Id);
        }

        var list = (await org.GetFromJsonAsync<ClubListResponse>($"/api/clubs?q=Truncation%20{suffix}", JsonOptions))!;
        Assert.Equal(BrowseClubsHandler.MaxResults, list.Items.Count);
        Assert.Equal(seeded, list.Total);
    }

    [Fact]
    public async Task Browse_Summary_AudienceSnapshotFieldsPopulated_FromClubAndEvents()
    {
        // STOR-69 redo Phase 1 acceptance criterion: a club with foundedYear 2019,
        // audience years [2,3,4], and two events with distinct typical attendance and
        // overlapping support needs produces foundedYear=2019, audienceYears=[2,3,4],
        // eventAttendanceSummary={min:40,max:120}, supportNeeds=["Catering","Venue","Volunteers"]
        // (deduped case-insensitively and sorted).
        var suffix = Guid.NewGuid().ToString("N")[..8];
        using var clubClient = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        using var org = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));

        var body = ValidBody($"Snapshot {suffix}");
        body["foundedYear"] = 2019;
        body["audience"] = new { fieldsOfStudy = new[] { "Computer Science" }, years = new[] { 4, 2, 3, 3 } };
        body["events"] = new object[]
        {
            EventBody("Big Event", attendance: 120, support: new[] { "Venue", "Volunteers" }),
            EventBody("Small Event", attendance: 40, support: new[] { "Venue", "Food and drink" }),
        };

        var saveResp = await clubClient.PutAsJsonAsync("/api/clubs/profile", body);
        Assert.True(saveResp.IsSuccessStatusCode, $"Save failed with {saveResp.StatusCode}: {await saveResp.Content.ReadAsStringAsync()}");
        await PostProfileActionAsync(clubClient, "publish");

        var list = (await org.GetFromJsonAsync<ClubListResponse>($"/api/clubs?q=Snapshot%20{suffix}", JsonOptions))!;
        var summary = Assert.Single(list.Items);

        Assert.Equal(2019, summary.FoundedYear);
        Assert.Equal(new[] { 2, 3, 4 }, summary.AudienceYears);
        Assert.Equal(new ClubEventAttendanceSummary(40, 120), summary.EventAttendanceSummary);
        Assert.Equal(new[] { "Food and drink", "Venue", "Volunteers" }, summary.SupportNeeds);
        Assert.Equal(1, list.Total);
    }

    [Fact]
    public async Task GetById_ReturnsPublished_And404ForDraftUnknownAndOwnerAccountId()
    {
        var club = await SeedUserAsync(ActorTypes.Club);
        using var clubClient = await ClientForAsync(club);
        using var org = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        var saved = await SaveAsync(clubClient, ValidBody("Visible Club"));

        var draft = await org.GetAsync($"/api/clubs/{saved.Id}");
        Assert.Equal(HttpStatusCode.NotFound, draft.StatusCode);
        Assert.Equal("club_profile_not_found", await ErrorCodeAsync(draft));

        await PostProfileActionAsync(clubClient, "publish");
        var ok = await org.GetAsync($"/api/clubs/{saved.Id}");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(saved.Id, (await ok.Content.ReadFromJsonAsync<ClubProfileResponse>(JsonOptions))!.Id);

        Assert.Equal(HttpStatusCode.NotFound, (await org.GetAsync($"/api/clubs/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await org.GetAsync($"/api/clubs/{club.Id}")).StatusCode);

        await PostProfileActionAsync(clubClient, "unpublish");
        Assert.Equal(HttpStatusCode.NotFound, (await org.GetAsync($"/api/clubs/{saved.Id}")).StatusCode);
    }

    [Fact]
    public async Task Clubs_CannotReadOrAffectEachOthersProfile()
    {
        using var clubA = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        using var clubB = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        var a = await SaveAsync(clubA, ValidBody("Club A"));

        // B has no profile: every own-profile call is 404 and never touches A's.
        Assert.Equal(HttpStatusCode.NotFound, (await clubB.GetAsync("/api/clubs/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clubB.PostAsync("/api/clubs/profile/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await clubB.PostAsync("/api/clubs/profile/unpublish", null)).StatusCode);

        var b = await SaveAsync(clubB, ValidBody("Club B"));
        Assert.NotEqual(a.Id, b.Id);
        await PostProfileActionAsync(clubB, "publish");

        var stillA = (await clubA.GetFromJsonAsync<ClubProfileResponse>("/api/clubs/profile", JsonOptions))!;
        Assert.Equal(a.Id, stillA.Id);
        Assert.Equal("Club A", stillA.Name);
        Assert.Equal("Draft", stillA.Status);
        Assert.Equal("Club B", (await clubB.GetFromJsonAsync<ClubProfileResponse>("/api/clubs/profile", JsonOptions))!.Name);
    }

    [Fact]
    public async Task Authorization_ClubEndpointsRejectStudentOrgAnonymous_CompanyEndpointsRejectClubStudentAnonymous()
    {
        var id = Guid.NewGuid();
        using var student = await ClientForAsync(await SeedUserAsync(ActorTypes.Student));
        using var org = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        using var club = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        using var anonymous = _factory.CreateClient();

        foreach (var client in new[] { student, org })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/clubs/profile")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/clubs/profile", ValidBody("x"))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/clubs/profile/publish", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/clubs/profile/unpublish", null)).StatusCode);
        }

        foreach (var client in new[] { student, club })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/clubs")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/clubs/{id}")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/clubs/profile")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PutAsJsonAsync("/api/clubs/profile", ValidBody("x"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/clubs/profile/publish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/clubs/profile/unpublish", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/clubs")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/clubs/{id}")).StatusCode);
    }

    [Fact]
    public async Task Responses_NeverContainOwnerAccountIdOrEmail()
    {
        var club = await SeedUserAsync(ActorTypes.Club);
        using var clubClient = await ClientForAsync(club);
        using var org = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        var put = await clubClient.PutAsJsonAsync("/api/clubs/profile", ValidBody("Leak Check"));
        var publish = await clubClient.PostAsync("/api/clubs/profile/publish", null);
        var own = await clubClient.GetAsync("/api/clubs/profile");
        var saved = (await own.Content.ReadFromJsonAsync<ClubProfileResponse>(JsonOptions))!;
        var list = await org.GetAsync("/api/clubs?q=Leak%20Check");
        var single = await org.GetAsync($"/api/clubs/{saved.Id}");

        foreach (var response in new[] { put, publish, own, list, single })
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(club.Id.ToString(), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(club.Email, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ownerAccountId", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("email", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Dictionary<string, object?> ValidBody(string name) => new()
    {
        ["name"] = name,
        ["tagline"] = null,
        ["about"] = "A student club that meets every week to learn and build together.",
        ["university"] = "Dhaka University",
        ["city"] = null,
        ["foundedYear"] = null,
        ["memberCount"] = 120,
        ["audience"] = new { fieldsOfStudy = new[] { "Computer Science", "Engineering", "computer science" }, years = new[] { 3, 1, 2 } },
        ["events"] = Array.Empty<object>(),
    };

    private static object EventBody(
        string title,
        string? description = null,
        int attendance = 50,
        string frequency = "Termly",
        string[]? support = null) => new
        {
            title,
            description,
            typicalAttendance = attendance,
            frequency,
            supportNeeds = support ?? new[] { "Funding" },
        };

    private static async Task<ClubProfileResponse> SaveAsync(HttpClient client, Dictionary<string, object?> body)
    {
        var response = await client.PutAsJsonAsync("/api/clubs/profile", body);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ClubProfileResponse>(JsonOptions))!;
    }

    private static async Task<ClubProfileResponse> PostProfileActionAsync(HttpClient client, string action)
    {
        var response = await client.PostAsync($"/api/clubs/profile/{action}", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ClubProfileResponse>(JsonOptions))!;
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
        var tokens = await tokenService.IssueTokensAsync(user, "club-profile-test", CancellationToken.None);
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
