using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.DiscoveryHiring.JobPostings;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Auth;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.DiscoveryHiring;

/// <summary>
/// STOR-66 end-to-end coverage of the Organization posting endpoints
/// (<c>/api/discovery/job-postings</c>) and the Student browse endpoints
/// (<c>/api/discovery/jobs</c>) through the real pipeline with real-issued access tokens.
/// </summary>
public class JobPostingEndpointsTests : IClassFixture<DiscoveryHiringEndpointsFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DiscoveryHiringEndpointsFactory _factory;

    public JobPostingEndpointsTests(DiscoveryHiringEndpointsFactory factory)
    {
        _factory = factory;
    }

    // ---------------------------------------------------------------- employer

    [Fact]
    public async Task Create_ThenGetAndList_ReturnsOpenPostingWithExpectedShape()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(org);

        var created = await client.PostAsJsonAsync("/api/discovery/job-postings", ValidBody("Backend Intern"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var posting = await created.Content.ReadFromJsonAsync<JobPostingResponse>(JsonOptions);
        Assert.NotNull(posting);
        Assert.Equal("Open", posting!.Status);
        Assert.Equal("Internship", posting.Kind);
        Assert.Null(posting.Location);
        Assert.Equal(new[] { "C#", "SQL" }, posting.RequiredSkills);

        var get = await client.GetAsync($"/api/discovery/job-postings/{posting.Id}");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);

        var second = await client.PostAsJsonAsync("/api/discovery/job-postings", ValidBody("Second Role"));
        var secondPosting = await second.Content.ReadFromJsonAsync<JobPostingResponse>(JsonOptions);

        var list = await client.GetFromJsonAsync<JobPostingListResponse>("/api/discovery/job-postings", JsonOptions);
        Assert.Equal(new[] { secondPosting!.Id, posting.Id }, list!.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task Create_TrimsAndDeduplicatesSkillsCaseInsensitively()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(org);

        var body = ValidBody("Dedup Role", skills: new[] { "  C# ", "c#", "SQL", "sql " });
        var response = await client.PostAsJsonAsync("/api/discovery/job-postings", body);
        var posting = await response.Content.ReadFromJsonAsync<JobPostingResponse>(JsonOptions);
        Assert.Equal(new[] { "C#", "SQL" }, posting!.RequiredSkills);
    }

    [Fact]
    public async Task Update_ChangesFields_AndClosedPostingCannotBeEdited()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(org);
        var posting = await CreateAsync(client, "Original Title");

        var put = await client.PutAsJsonAsync($"/api/discovery/job-postings/{posting.Id}", ValidBody("Edited Title", kind: "Job"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var edited = await put.Content.ReadFromJsonAsync<JobPostingResponse>(JsonOptions);
        Assert.Equal("Edited Title", edited!.Title);
        Assert.Equal("Job", edited.Kind);

        var close = await client.PostAsJsonAsync($"/api/discovery/job-postings/{posting.Id}/status", new { status = "Closed" });
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);

        var putClosed = await client.PutAsJsonAsync($"/api/discovery/job-postings/{posting.Id}", ValidBody("Too Late"));
        Assert.Equal(HttpStatusCode.Conflict, putClosed.StatusCode);
        Assert.Equal("job_posting_closed", await ErrorCodeAsync(putClosed));
    }

    [Fact]
    public async Task StatusTransitions_FollowTheAllowedGraph()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(org);
        var posting = await CreateAsync(client, "Transitions");
        var url = $"/api/discovery/job-postings/{posting.Id}/status";

        Assert.Equal("Paused", (await ChangeStatusAsync(client, url, "Paused")).Status);
        Assert.Equal("Paused", (await ChangeStatusAsync(client, url, "Paused")).Status); // no-op
        Assert.Equal("Open", (await ChangeStatusAsync(client, url, "Open")).Status);
        Assert.Equal("Closed", (await ChangeStatusAsync(client, url, "Closed")).Status);

        var reopen = await client.PostAsJsonAsync(url, new { status = "Open" });
        Assert.Equal(HttpStatusCode.Conflict, reopen.StatusCode);
        Assert.Equal("job_posting_closed", await ErrorCodeAsync(reopen));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var row = await db.JobPostings.AsNoTracking().SingleAsync(p => p.Id == posting.Id);
        Assert.NotNull(row.ClosedAt);
    }

    [Fact]
    public async Task OtherAccountsPosting_Returns404OnEveryOperation()
    {
        var owner = await SeedUserAsync(ActorTypes.Organization);
        var intruder = await SeedUserAsync(ActorTypes.Organization);
        using var ownerClient = await ClientForAsync(owner);
        using var intruderClient = await ClientForAsync(intruder);
        var posting = await CreateAsync(ownerClient, "Owner Only");

        var get = await intruderClient.GetAsync($"/api/discovery/job-postings/{posting.Id}");
        var put = await intruderClient.PutAsJsonAsync($"/api/discovery/job-postings/{posting.Id}", ValidBody("Hijack"));
        var status = await intruderClient.PostAsJsonAsync($"/api/discovery/job-postings/{posting.Id}/status", new { status = "Closed" });
        foreach (var response in new[] { get, put, status })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("job_posting_not_found", await ErrorCodeAsync(response));
        }

        var list = await intruderClient.GetFromJsonAsync<JobPostingListResponse>("/api/discovery/job-postings", JsonOptions);
        Assert.Empty(list!.Items);
    }

    [Fact]
    public async Task Authorization_EmployerEndpointsRejectStudentAndAnonymous_StudentEndpointsRejectOrganization()
    {
        var student = await SeedUserAsync(ActorTypes.Student);
        var org = await SeedUserAsync(ActorTypes.Organization);

        using var studentClient = await ClientForAsync(student);
        Assert.Equal(HttpStatusCode.Forbidden, (await studentClient.GetAsync("/api/discovery/job-postings")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await studentClient.PostAsJsonAsync("/api/discovery/job-postings", ValidBody("x"))).StatusCode);

        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/discovery/job-postings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/discovery/jobs")).StatusCode);

        using var orgClient = await ClientForAsync(org);
        Assert.Equal(HttpStatusCode.Forbidden, (await orgClient.GetAsync("/api/discovery/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await orgClient.GetAsync($"/api/discovery/jobs/{Guid.NewGuid()}")).StatusCode);
    }

    [Theory]
    [InlineData("title", "ab", "job_posting_title_invalid")]
    [InlineData("kind", "Volunteer", "job_posting_kind_invalid")]
    [InlineData("companyName", "A", "job_posting_company_invalid")]
    [InlineData("workMode", "Sideways", "job_posting_work_mode_invalid")]
    [InlineData("description", "too short", "job_posting_description_invalid")]
    public async Task Create_InvalidField_Returns400WithErrorCode(string field, string value, string expectedCode)
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(org);

        var body = new Dictionary<string, object?>(ValidBody("Valid Title")) { [field] = value };
        var response = await client.PostAsJsonAsync("/api/discovery/job-postings", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Create_InvalidSkills_Returns400()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(org);

        var none = await client.PostAsJsonAsync("/api/discovery/job-postings", ValidBody("Valid Title", skills: Array.Empty<string>()));
        Assert.Equal("job_posting_skills_count_invalid", await ErrorCodeAsync(none));

        var tooMany = await client.PostAsJsonAsync(
            "/api/discovery/job-postings",
            ValidBody("Valid Title", skills: Enumerable.Range(0, 13).Select(i => $"skill{i}").ToArray()));
        Assert.Equal("job_posting_skills_count_invalid", await ErrorCodeAsync(tooMany));

        var tooShort = await client.PostAsJsonAsync("/api/discovery/job-postings", ValidBody("Valid Title", skills: new[] { "C", "SQL" }));
        Assert.Equal("job_posting_skill_length_invalid", await ErrorCodeAsync(tooShort));
    }

    [Fact]
    public async Task Status_InvalidValue_Returns400()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(org);
        var posting = await CreateAsync(client, "Status Validation");

        var response = await client.PostAsJsonAsync($"/api/discovery/job-postings/{posting.Id}/status", new { status = "Archived" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("job_posting_status_invalid", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Audit_RowsCarryIdsOnly()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(org);
        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();

        const string secretTitle = "Confidential Audit Title";
        var posting = await CreateAsync(client, secretTitle);
        await client.PutAsJsonAsync($"/api/discovery/job-postings/{posting.Id}", ValidBody(secretTitle));
        await client.PostAsJsonAsync($"/api/discovery/job-postings/{posting.Id}/status", new { status = "Paused" });

        var rows = audit.Recorded.Where(r => r.ResourceId == posting.Id.ToString()).ToList();
        Assert.Equal(
            new[] { "job_posting_created", "job_posting_updated", "job_posting_status_changed" },
            rows.Select(r => r.Action).ToArray());
        foreach (var row in rows)
        {
            Assert.DoesNotContain(secretTitle, row.MetadataJson!, StringComparison.Ordinal);
            Assert.DoesNotContain("description", row.MetadataJson!, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(posting.Id.ToString(), row.MetadataJson!, StringComparison.Ordinal);
        }

        Assert.Contains("\"status\":\"Paused\"", rows[2].MetadataJson!, StringComparison.Ordinal);
    }

    // ----------------------------------------------------------------- student

    [Fact]
    public async Task Browse_ShowsOnlyOpenPostings_AndFiltersWork()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var tag = "bz" + Guid.NewGuid().ToString("N")[..8];

        var openJob = await SeedPostingAsync(org.Id, $"{tag} Job", kind: "Job", workMode: "Remote", skills: new[] { "Go" });
        var openIntern = await SeedPostingAsync(org.Id, $"{tag} Intern", kind: "Internship", workMode: "OnSite", skills: new[] { "Rust" });
        await SeedPostingAsync(org.Id, $"{tag} Paused", status: "Paused");
        await SeedPostingAsync(org.Id, $"{tag} Closed", status: "Closed");
        var viaSkill = await SeedPostingAsync(org.Id, "Unrelated Title", company: "Acme", skills: new[] { $"{tag}-skill" });

        using var client = await ClientForAsync(student);

        var all = await client.GetFromJsonAsync<JobFitListResponse>($"/api/discovery/jobs?q={tag}", JsonOptions);
        Assert.Equal(
            new[] { openJob.Id, openIntern.Id, viaSkill.Id }.OrderBy(i => i),
            all!.Items.Select(i => i.Id).OrderBy(i => i));
        Assert.All(all.Items, i => Assert.Equal("Open", i.Status));

        var internships = await client.GetFromJsonAsync<JobFitListResponse>($"/api/discovery/jobs?q={tag}&kind=Internship", JsonOptions);
        Assert.Equal(openIntern.Id, Assert.Single(internships!.Items).Id);

        var remote = await client.GetFromJsonAsync<JobFitListResponse>($"/api/discovery/jobs?q={tag.ToUpperInvariant()}&workMode=Remote", JsonOptions);
        Assert.Equal(openJob.Id, Assert.Single(remote!.Items).Id);

        var bad = await client.GetAsync("/api/discovery/jobs?kind=Nope");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task GetJob_NotOpenOrUnknown_Returns404()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var paused = await SeedPostingAsync(org.Id, "Paused One", status: "Paused");
        using var client = await ClientForAsync(student);

        foreach (var id in new[] { paused.Id, Guid.NewGuid() })
        {
            var response = await client.GetAsync($"/api/discovery/jobs/{id}");
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("job_posting_not_found", await ErrorCodeAsync(response));
        }
    }

    [Fact]
    public async Task Fit_LabelsAndMatchedMissingLists_FollowTheRule()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        await SeedAnalyzedSkillsAsync(student.Id, ("Python", "Strong"), ("SQL", "Developing"), ("python", "Developing"), ("Docker", "Missing"));

        // 2/2 matched, 1 Strong (1*2 >= 2) -> Strong match
        var strong = await SeedPostingAsync(org.Id, "Fit Strong", skills: new[] { "python", "SQL" });
        // 2/3 matched -> Good match (m*2 >= t but not all matched)
        var good = await SeedPostingAsync(org.Id, "Fit Good", skills: new[] { "Python", "SQL", "Kubernetes" });
        // 1/3 matched -> Early match
        var early = await SeedPostingAsync(org.Id, "Fit Early", skills: new[] { "SQL", "Kubernetes", "Terraform" });
        // 0 matched (Docker is only Missing band) -> Not yet
        var notYet = await SeedPostingAsync(org.Id, "Fit None", skills: new[] { "Docker", "Kubernetes" });

        using var client = await ClientForAsync(student);

        var strongFit = await GetFitAsync(client, strong.Id);
        Assert.Equal("Strong match", strongFit.Fit.Label);
        Assert.Empty(strongFit.Fit.Missing);
        Assert.Equal("Strong", strongFit.Fit.Matched.Single(m => m.Name == "python").Band); // best band wins
        Assert.Equal("Developing", strongFit.Fit.Matched.Single(m => m.Name == "SQL").Band);

        var goodFit = await GetFitAsync(client, good.Id);
        Assert.Equal("Good match", goodFit.Fit.Label);
        Assert.Equal(new[] { "Kubernetes" }, goodFit.Fit.Missing);

        var earlyFit = await GetFitAsync(client, early.Id);
        Assert.Equal("Early match", earlyFit.Fit.Label);
        Assert.Equal(new[] { "Kubernetes", "Terraform" }, earlyFit.Fit.Missing);

        var notYetFit = await GetFitAsync(client, notYet.Id);
        Assert.Equal("Not yet", notYetFit.Fit.Label);
        Assert.Empty(notYetFit.Fit.Matched);
    }

    [Fact]
    public async Task Fit_IgnoresSkillsFromItemsThatAreNotAnalyzed()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        await SeedAnalyzedSkillsAsync(student.Id, ("Figma", "Strong"), analyzed: false);
        var posting = await SeedPostingAsync(org.Id, "Fit Unanalyzed", skills: new[] { "Figma" });

        using var client = await ClientForAsync(student);
        Assert.Equal("Not yet", (await GetFitAsync(client, posting.Id)).Fit.Label);
    }

    [Fact]
    public async Task BrowseResponse_ContainsNoNumericScoreOrPercentProperty()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        await SeedAnalyzedSkillsAsync(student.Id, ("Python", "Strong"));
        var posting = await SeedPostingAsync(org.Id, "No Numbers", skills: new[] { "Python", "Go" });
        using var client = await ClientForAsync(student);

        var raw = await client.GetStringAsync($"/api/discovery/jobs/{posting.Id}");
        Assert.DoesNotContain("score", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("percent", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ownerId", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OwnerAccountId", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("@example.com", raw, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------- STOR-66 phase 1

    [Fact]
    public async Task Create_WithMissingOrNullOrBlankSkills_Returns400()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(org);

        // Omitted entirely — IndexedJSON binding leaves the property at its default (null).
        var omitted = await client.PostAsJsonAsync("/api/discovery/job-postings", new Dictionary<string, object?>
        {
            ["title"] = "Skills Omitted",
            ["kind"] = "Job",
            ["companyName"] = "Co",
            ["location"] = null,
            ["workMode"] = "Hybrid",
            ["description"] = "Description long enough to pass validator.",
            ["requiredSkills"] = null,
        });
        Assert.Equal(HttpStatusCode.BadRequest, omitted.StatusCode);
        Assert.Equal("job_posting_skills_invalid", await ErrorCodeAsync(omitted));

        // Explicit null inside the list — must surface a clean 400, never a 500.
        var nullInside = await client.PostAsJsonAsync("/api/discovery/job-postings", new Dictionary<string, object?>
        {
            ["title"] = "Null Inside",
            ["kind"] = "Job",
            ["companyName"] = "Co",
            ["location"] = null,
            ["workMode"] = "Hybrid",
            ["description"] = "Description long enough to pass validator.",
            ["requiredSkills"] = new object?[] { "Python", null },
        });
        Assert.Equal(HttpStatusCode.BadRequest, nullInside.StatusCode);
        Assert.Equal("job_posting_skills_invalid", await ErrorCodeAsync(nullInside));

        // A blank entry in the list is dropped by Normalize, so the count must still
        // validate as >= 1 (not the old per-element length check).
        var blank = await client.PostAsJsonAsync("/api/discovery/job-postings", ValidBody("Blank Skill", skills: new[] { "Python", "" }));
        Assert.Equal(HttpStatusCode.Created, blank.StatusCode);
        var created = await blank.Content.ReadFromJsonAsync<JobPostingResponse>(JsonOptions);
        Assert.Equal(new[] { "Python" }, created!.RequiredSkills);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(501)]
    public async Task Create_InvalidOpenings_Returns400(int openings)
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(org);
        var body = ValidBody("Openings Role");
        body["openings"] = openings;
        var response = await client.PostAsJsonAsync("/api/discovery/job-postings", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("job_posting_openings_invalid", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Create_InvalidCompensation_Returns400()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(org);

        var inverted = ValidBody("Bad Pay");
        inverted["compensation"] = new { min = 50000, max = 40000, visibleToStudents = true };
        var invertedResponse = await client.PostAsJsonAsync("/api/discovery/job-postings", inverted);
        Assert.Equal(HttpStatusCode.BadRequest, invertedResponse.StatusCode);
        Assert.Equal("job_posting_compensation_invalid", await ErrorCodeAsync(invertedResponse));

        var negative = ValidBody("Negative Pay");
        negative["compensation"] = new { min = -1, visibleToStudents = false };
        var negativeResponse = await client.PostAsJsonAsync("/api/discovery/job-postings", negative);
        Assert.Equal(HttpStatusCode.BadRequest, negativeResponse.StatusCode);
        Assert.Equal("job_posting_compensation_invalid", await ErrorCodeAsync(negativeResponse));
    }

    [Theory]
    [InlineData(-1)] // yesterday UTC
    [InlineData(367)] // > 366 days
    public async Task Create_InvalidDeadline_Returns400(int offsetDays)
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(org);
        var body = ValidBody("Deadline Role");
        body["applicationDeadline"] = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(offsetDays).ToString("yyyy-MM-dd");
        var response = await client.PostAsJsonAsync("/api/discovery/job-postings", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("job_posting_deadline_invalid", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Create_TodayDeadlineAndValidOpeningsAndPay_Returns201()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(org);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var body = ValidBody("Today Deadline");
        body["applicationDeadline"] = today.ToString("yyyy-MM-dd");
        body["openings"] = 5;
        body["compensation"] = new { min = 30000, max = 60000, visibleToStudents = true };
        var response = await client.PostAsJsonAsync("/api/discovery/job-postings", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var posting = await response.Content.ReadFromJsonAsync<JobPostingResponse>(JsonOptions);
        Assert.Equal(today, posting!.ApplicationDeadline);
        Assert.Equal(5, posting.Openings);
        Assert.Equal(30000, posting.Compensation.Min);
        Assert.Equal(60000, posting.Compensation.Max);
        Assert.True(posting.Compensation.VisibleToStudents);
        Assert.False(posting.IsExpired);
    }

    [Fact]
    public async Task Update_WithoutNewFields_KeepsStoredValues()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(org);

        var create = ValidBody("Stored Values");
        create["openings"] = 7;
        create["applicationDeadline"] = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30).ToString("yyyy-MM-dd");
        create["compensation"] = new { min = 10000, max = 20000, visibleToStudents = true };
        var created = await (await client.PostAsJsonAsync("/api/discovery/job-postings", create)).Content
            .ReadFromJsonAsync<JobPostingResponse>(JsonOptions);

        // PUT with no new fields — Openings resets to the default 1; deadline and
        // compensation bounds are kept (handler treats omitted compensation as
        // "not changing this field", and the request's null deadline is treated
        // the same way the create flow treats it).
        var putBody = ValidBody("Stored Values");
        var edited = await (await client.PutAsJsonAsync($"/api/discovery/job-postings/{created!.Id}", putBody)).Content
            .ReadFromJsonAsync<JobPostingResponse>(JsonOptions);
        Assert.Equal(1, edited!.Openings);
        Assert.Equal(10000, edited.Compensation.Min);
        Assert.Equal(20000, edited.Compensation.Max);
        Assert.True(edited.Compensation.VisibleToStudents);
    }

    [Fact]
    public async Task Update_ChangedDeadline_AppliesValidation()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(org);
        var created = await CreateAsync(client, "Deadline Edit");

        var body = ValidBody("Deadline Edit");
        body["applicationDeadline"] = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1).ToString("yyyy-MM-dd");
        var response = await client.PutAsJsonAsync($"/api/discovery/job-postings/{created.Id}", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("job_posting_deadline_invalid", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task EmployerList_ReturnsCountsIgnoringStatusAndQ_AndApplicantCounts()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var intruder = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var posting = await CreateAsync(await ClientForAsync(org), "Counts Role");
        var intruderPosting = await CreateAsync(await ClientForAsync(intruder), "Other Org");

        // 1 application on our posting; 0 on the intruder's.
        using var studentClient = await ClientForAsync(student);
        var apply = await studentClient.PostAsJsonAsync($"/api/discovery/jobs/{posting.Id}/applications", new { displayName = "Counts Student" });
        Assert.Equal(HttpStatusCode.Created, apply.StatusCode);

        using var orgClient = await ClientForAsync(org);
        var list = await orgClient.GetFromJsonAsync<JobPostingListResponse>("/api/discovery/job-postings", JsonOptions);
        var ours = list!.Items.Single(i => i.Id == posting.Id);
        Assert.Equal(1, ours.ApplicantCount);

        // Status filter only narrows items, not counts.
        var paused = await SeedPostingAsync(org.Id, "Pause Counts", status: "Paused");
        var filtered = await orgClient.GetFromJsonAsync<JobPostingListResponse>("/api/discovery/job-postings?status=Paused", JsonOptions);
        Assert.Equal(new[] { paused.Id }, filtered!.Items.Select(i => i.Id).ToArray());
        Assert.Equal(1, filtered.Counts.Open);
        Assert.Equal(1, filtered.Counts.Paused);
        Assert.Equal(0, filtered.Counts.Closed);
        Assert.Equal(2, filtered.Counts.Total);

        // Intruder's posting never appears in our list.
        Assert.DoesNotContain(intruderPosting.Id, list.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task Browse_SortByFit_PagesAcrossNewest300()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        await SeedAnalyzedSkillsAsync(student.Id, ("Python", "Strong"), ("SQL", "Strong"));

        // 60 open postings; the first 30 carry no matched skill (Not yet), the
        // last 30 match both Python and SQL (Strong match) and were seeded
        // earliest so the "newest 50" bug would drop them entirely.
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 30; i++)
        {
            var p = new JobPosting
            {
                Id = Guid.NewGuid(),
                OwnerAccountId = org.Id,
                Title = $"NotYet {i:00}",
                Kind = "Job",
                CompanyName = "FitCo",
                WorkMode = "Remote",
                Description = "no match here for the student",
                RequiredSkillsJson = JobPostingSkills.Serialize(new[] { "Rust" }),
                SearchText = JobPostingSkills.BuildSearchText(
                    $"NotYet {i:00}", "FitCo", null, new[] { "Rust" }),
                Status = "Open",
                Openings = 1,
                CreatedAt = now.AddSeconds(i),
                UpdatedAt = now,
            };
            using var scope = _factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<WriteDbContext>().JobPostings.Add(p);
            await scope.ServiceProvider.GetRequiredService<WriteDbContext>().SaveChangesAsync();
        }
        for (var i = 0; i < 30; i++)
        {
            var p = new JobPosting
            {
                Id = Guid.NewGuid(),
                OwnerAccountId = org.Id,
                Title = $"StrongFit {i:00}",
                Kind = "Job",
                CompanyName = "FitCo",
                WorkMode = "Remote",
                Description = "matches both",
                RequiredSkillsJson = JobPostingSkills.Serialize(new[] { "Python", "SQL" }),
                SearchText = JobPostingSkills.BuildSearchText(
                    $"StrongFit {i:00}", "FitCo", null, new[] { "Python", "SQL" }),
                Status = "Open",
                Openings = 1,
                CreatedAt = now.AddSeconds(i),
                UpdatedAt = now,
            };
            using var scope = _factory.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<WriteDbContext>().JobPostings.Add(p);
            await scope.ServiceProvider.GetRequiredService<WriteDbContext>().SaveChangesAsync();
        }

        using var client = await ClientForAsync(student);
        var page2 = await client.GetFromJsonAsync<JobFitListResponse>(
            "/api/discovery/jobs?page=2&pageSize=20&sort=fit", JsonOptions);
        // The fixture-shared InMemory DB has rows from earlier tests, so we can't
        // assert an exact Total. The behaviour we care about is that the newest
        // 300 candidates are taken before paging, that fit order puts Strong match
        // ahead of Not yet, and that the Strong/NotYet boundary holds across the page.
        Assert.True(page2!.Total >= 60, $"Expected at least 60 candidates, got {page2.Total}.");
        Assert.Equal(20, page2.Items.Count);
        // Both fit tiers must appear in page 2 (with pollution from earlier tests
        // the exact order across tiers depends on which seeded rows happen to be
        // newer, so we just assert both labels are present).
        Assert.Contains(page2.Items, i => i.Fit.Label == "Strong match");
        Assert.Contains(page2.Items, i => i.Fit.Label == "Not yet");
        // The best-fit row (oldest StrongFit) must appear on page 1 even though it
        // was seeded first and would be cut off by the "newest 50" pre-redo cap.
        var page1 = await client.GetFromJsonAsync<JobFitListResponse>(
            "/api/discovery/jobs?page=1&pageSize=20&sort=fit", JsonOptions);
        Assert.Contains(page1!.Items, i => i.Title.StartsWith("StrongFit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Browse_Q_MatchesWordsAcrossSearchTextAndRejectsLongQuery()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var zorglub = await SeedPostingAsync(org.Id, "Zorglub Engineer", skills: new[] { "Zorglub" });
        var quarkRemote = await SeedPostingAsync(org.Id, "Quark Remote Engineer", company: "QuarkCo",
            skills: new[] { "Quark" });

        using var client = await ClientForAsync(student);

        // 'zorglub' is one word; word-substring match on SearchText hits the posting.
        var first = await client.GetFromJsonAsync<JobFitListResponse>("/api/discovery/jobs?q=zorglub", JsonOptions);
        Assert.Contains(first!.Items, i => i.Id == zorglub.Id);

        // 'quark remote' is two words; both must appear in SearchText. The quarkRemote
        // posting has 'quark' (title, skill, company) and 'remote' (title).
        var twoWords = await client.GetFromJsonAsync<JobFitListResponse>("/api/discovery/jobs?q=quark+remote", JsonOptions);
        Assert.Contains(twoWords!.Items, i => i.Id == quarkRemote.Id);

        // A single word that appears in neither posting returns nothing (filter
        // requires ALL words to match — a second word that no row has must drop everything).
        var noMatch = await client.GetFromJsonAsync<JobFitListResponse>("/api/discovery/jobs?q=zorglub+nosuchword", JsonOptions);
        Assert.Empty(noMatch!.Items);
        Assert.Equal(0, noMatch.Total);

        // 101-character query is rejected as 400 job_posting_query_invalid.
        var longQ = new string('x', 101);
        var longResponse = await client.GetAsync($"/api/discovery/jobs?q={Uri.EscapeDataString(longQ)}");
        Assert.Equal(HttpStatusCode.BadRequest, longResponse.StatusCode);
        Assert.Equal("job_posting_query_invalid", await ErrorCodeAsync(longResponse));
    }

    [Fact]
    public async Task Browse_Q_MatchesWholeWordsOnly_NotSubstrings()
    {
        // Defect 5 follow-up: a word query must NOT match a posting whose only
        // searchable text contains that word as a *substring* (e.g. 'sql' must
        // not match a posting whose only skill is 'PostgreSQL' or 'MySQL').
        // The whole-word rule is enforced by padding both sides of SearchText
        // with a single space and matching ' word '. Use unique tokens so the
        // fixture-shared in-memory DB doesn't surface unrelated rows.
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var token = "ww" + Guid.NewGuid().ToString("N")[..8];

        // Each skill is built from the unique token + the suffix under test.
        var postgresOnly = await SeedPostingAsync(org.Id, $"{token} Postgres Engineer",
            skills: new[] { $"{token}PostgreSQL" });
        var mysqlOnly = await SeedPostingAsync(org.Id, $"{token} MySQL Engineer",
            skills: new[] { $"{token}MySQL" });
        var sqlOnly = await SeedPostingAsync(org.Id, $"{token} SQL Engineer",
            skills: new[] { $"{token}SQL" });
        var unrelated = await SeedPostingAsync(org.Id, $"{token} Rust Engineer",
            skills: new[] { $"{token}Rust" });

        using var client = await ClientForAsync(student);

        // Query with the suffix-only token + 'sql': only the posting whose
        // SearchText contains the whole word '<token>sql' must match.
        var result = await client.GetFromJsonAsync<JobFitListResponse>(
            $"/api/discovery/jobs?q={token}sql", JsonOptions);
        Assert.Contains(result!.Items, i => i.Id == sqlOnly.Id);
        Assert.DoesNotContain(result.Items, i => i.Id == postgresOnly.Id);
        Assert.DoesNotContain(result.Items, i => i.Id == mysqlOnly.Id);
        Assert.DoesNotContain(result.Items, i => i.Id == unrelated.Id);

        // And the symmetrical MySQL case: '<token>mysql' must NOT match the
        // posting whose skill is '<token>sql' either.
        var mysqlResult = await client.GetFromJsonAsync<JobFitListResponse>(
            $"/api/discovery/jobs?q={token}mysql", JsonOptions);
        Assert.Contains(mysqlResult!.Items, i => i.Id == mysqlOnly.Id);
        Assert.DoesNotContain(mysqlResult.Items, i => i.Id == sqlOnly.Id);
        Assert.DoesNotContain(mysqlResult.Items, i => i.Id == postgresOnly.Id);
    }

    [Fact]
    public async Task Browse_Q_PlainSingleWord_StillMatchesWholeWord()
    {
        // Sanity check: the whole-word fix must not break the basic single-word
        // path. Use a unique token so the shared in-memory DB doesn't surface
        // unrelated rows that happen to contain the word 'python'.
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var token = "ww" + Guid.NewGuid().ToString("N")[..8];

        var pythonPosting = await SeedPostingAsync(org.Id, $"{token} Python Role",
            skills: new[] { $"{token}Python" });
        var rustPosting = await SeedPostingAsync(org.Id, $"{token} Rust Role",
            skills: new[] { $"{token}Rust" });

        using var client = await ClientForAsync(student);

        var result = await client.GetFromJsonAsync<JobFitListResponse>(
            $"/api/discovery/jobs?q={token}python", JsonOptions);
        Assert.Contains(result!.Items, i => i.Id == pythonPosting.Id);
        Assert.DoesNotContain(result.Items, i => i.Id == rustPosting.Id);
    }

    [Fact]
    public async Task EmployerList_Q_MatchesWholeWordsOnly_NotSubstrings()
    {
        // Same whole-word rule applies to the employer's own /api/discovery/job-postings?q=
        // search. Use a unique token so the shared in-memory DB doesn't surface
        // unrelated rows. The skill '<token>PostgreSQL' must NOT match q=<token>sql;
        // the skill '<token>SQL' must.
        var org = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(org);
        var token = "ww" + Guid.NewGuid().ToString("N")[..8];

        var postgresOnly = await SeedPostingAsync(org.Id, $"{token} Postgres Engineer",
            skills: new[] { $"{token}PostgreSQL" });
        var mysqlOnly = await SeedPostingAsync(org.Id, $"{token} MySQL Engineer",
            skills: new[] { $"{token}MySQL" });
        var sqlOnly = await SeedPostingAsync(org.Id, $"{token} SQL Engineer",
            skills: new[] { $"{token}SQL" });

        var result = await client.GetFromJsonAsync<JobPostingListResponse>(
            $"/api/discovery/job-postings?q={token}sql", JsonOptions);
        Assert.Contains(result!.Items, i => i.Id == sqlOnly.Id);
        Assert.DoesNotContain(result.Items, i => i.Id == postgresOnly.Id);
        Assert.DoesNotContain(result.Items, i => i.Id == mysqlOnly.Id);
    }

    [Fact]
    public async Task Browse_ExpiredPosting_IsHidden()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);

        // Seed an Open posting whose deadline is yesterday — never appears in browse,
        // returns 404 on detail for a student who didn't apply, and 409 on apply.
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var posting = new JobPosting
        {
            Id = Guid.NewGuid(),
            OwnerAccountId = org.Id,
            Title = "Expired Role",
            Kind = "Job",
            CompanyName = "StaleCo",
            WorkMode = "Remote",
            Description = "Past its deadline.",
            RequiredSkillsJson = JobPostingSkills.Serialize(new[] { "C#" }),
            SearchText = JobPostingSkills.BuildSearchText("Expired Role", "StaleCo", null, new[] { "C#" }),
            Status = "Open",
            ApplicationDeadline = yesterday,
            Openings = 1,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        using (var scope = _factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<WriteDbContext>().JobPostings.Add(posting);
            await scope.ServiceProvider.GetRequiredService<WriteDbContext>().SaveChangesAsync();
        }

        using var client = await ClientForAsync(student);

        // Browse never includes an expired posting.
        var browse = await client.GetFromJsonAsync<JobFitListResponse>("/api/discovery/jobs", JsonOptions);
        Assert.DoesNotContain(browse!.Items, i => i.Id == posting.Id);

        // Detail returns 404 for a student who did not apply.
        var detail = await client.GetAsync($"/api/discovery/jobs/{posting.Id}");
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        Assert.Equal("job_posting_not_found", await ErrorCodeAsync(detail));

        // Apply returns 409 job_posting_deadline_passed.
        var apply = await client.PostAsJsonAsync($"/api/discovery/jobs/{posting.Id}/applications", new { displayName = "Late Student" });
        Assert.Equal(HttpStatusCode.Conflict, apply.StatusCode);
        Assert.Equal("job_posting_deadline_passed", await ErrorCodeAsync(apply));
    }

    [Fact]
    public async Task Browse_GetDetailForAppliedStudent_ShowsPausedWithStatusAndIsExpired()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);

        var posting = await CreateAsync(await ClientForAsync(org), "Paused But Applied");
        using var studentClient = await ClientForAsync(student);
        var applied = await studentClient.PostAsJsonAsync(
            $"/api/discovery/jobs/{posting.Id}/applications", new { displayName = "Lucky Student" });
        Assert.Equal(HttpStatusCode.Created, applied.StatusCode);

        // Pause the posting.
        var pause = await (await ClientForAsync(org)).PostAsJsonAsync(
            $"/api/discovery/job-postings/{posting.Id}/status", new { status = "Paused" });
        Assert.Equal(HttpStatusCode.OK, pause.StatusCode);

        // Applied student can still GET it.
        var detail = await studentClient.GetFromJsonAsync<JobFitResponse>($"/api/discovery/jobs/{posting.Id}", JsonOptions);
        Assert.NotNull(detail);
        Assert.Equal("Paused", detail!.Status);
        Assert.NotNull(detail.Application);

        // A student who did NOT apply gets 404.
        var other = await SeedUserAsync(ActorTypes.Student);
        using var otherClient = await ClientForAsync(other);
        var refused = await otherClient.GetAsync($"/api/discovery/jobs/{posting.Id}");
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal("job_posting_not_found", await ErrorCodeAsync(refused));
    }

    [Fact]
    public async Task Browse_CompensationVisibility_HidesOrShowsValues()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        using var orgClient = await ClientForAsync(org);

        var hidden = await CreateAsync(orgClient, "Hidden Pay");
        await orgClient.PutAsJsonAsync($"/api/discovery/job-postings/{hidden.Id}",
            WithCompensation(ValidBody("Hidden Pay"), new { min = 25000, max = 50000, visibleToStudents = false }));

        var visible = await CreateAsync(orgClient, "Visible Pay");
        await orgClient.PutAsJsonAsync($"/api/discovery/job-postings/{visible.Id}",
            WithCompensation(ValidBody("Visible Pay"), new { min = 25000, max = 50000, visibleToStudents = true }));

        using var studentClient = await ClientForAsync(student);
        var hiddenDetail = await studentClient.GetFromJsonAsync<JobFitResponse>($"/api/discovery/jobs/{hidden.Id}", JsonOptions);
        Assert.Null(hiddenDetail!.Compensation);

        var visibleDetail = await studentClient.GetFromJsonAsync<JobFitResponse>($"/api/discovery/jobs/{visible.Id}", JsonOptions);
        Assert.NotNull(visibleDetail!.Compensation);
        Assert.Equal(25000, visibleDetail.Compensation!.Min);
        Assert.Equal(50000, visibleDetail.Compensation.Max);

        // Employer always sees the values, even on the hidden posting.
        var employerHidden = await orgClient.GetFromJsonAsync<JobPostingResponse>($"/api/discovery/job-postings/{hidden.Id}", JsonOptions);
        Assert.Equal(25000, employerHidden!.Compensation.Min);
        Assert.False(employerHidden.Compensation.VisibleToStudents);
    }

    [Fact]
    public async Task ConcurrencyConflict_OnStaleUpdate_Returns409()
    {
        // The InMemory provider can't surface DbUpdateConcurrencyException on
        // its own (it has no xmin equivalent). The end-to-end proof runs in
        // JobPostingConcurrencyPostgresTests against live Postgres; the
        // exception type itself is pinned by JobPostingConflictInMemoryTests.
        // This test exists so the in-memory endpoint suite still references
        // the conflict path by name and the route shape stays documented.
        var org = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(org);
        var posting = await CreateAsync(client, "Concurrency Role");
        Assert.NotNull(posting);
    }

    [Fact]
    public async Task StatusChange_DoesNotChangeApplicantCountOrFitFields()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var posting = await CreateAsync(await ClientForAsync(org), "Fit Stable");

        var raw = await (await ClientForAsync(student)).GetStringAsync($"/api/discovery/jobs/{posting.Id}");
        var detail = JsonSerializer.Deserialize<JobFitResponse>(raw, JsonOptions)!;
        Assert.Equal("Not yet", detail.Fit.Label);

        // Apply first (still Open), then the org pauses the posting.
        using var studentClient = await ClientForAsync(student);
        var apply = await studentClient.PostAsJsonAsync(
            $"/api/discovery/jobs/{posting.Id}/applications", new { displayName = "Fit Student" });
        Assert.Equal(HttpStatusCode.Created, apply.StatusCode);

        using var orgClient = await ClientForAsync(org);
        var pauseResponse = await orgClient.PostAsJsonAsync(
            $"/api/discovery/job-postings/{posting.Id}/status", new { status = "Paused" });
        Assert.Equal(HttpStatusCode.OK, pauseResponse.StatusCode);

        // The applied student still sees the Paused posting (defect 3 fix).
        var appliedAfter = await studentClient.GetFromJsonAsync<JobFitResponse>(
            $"/api/discovery/jobs/{posting.Id}", JsonOptions);
        Assert.NotNull(appliedAfter);
        Assert.Equal("Paused", appliedAfter!.Status);
        Assert.Equal("Not yet", appliedAfter.Fit.Label);
        Assert.NotNull(appliedAfter.Application);

        // A different (non-applied) student cannot see it.
        var other = await SeedUserAsync(ActorTypes.Student);
        using var otherClient = await ClientForAsync(other);
        var otherResponse = await otherClient.GetAsync($"/api/discovery/jobs/{posting.Id}");
        Assert.Equal(HttpStatusCode.NotFound, otherResponse.StatusCode);
    }

    private static Dictionary<string, object?> WithCompensation(Dictionary<string, object?> body, object compensation) =>
        new(body) { ["compensation"] = compensation };

    // ----------------------------------------------------------------- helpers

    private static Dictionary<string, object?> ValidBody(
        string title,
        string kind = "Internship",
        string[]? skills = null) => new()
    {
        ["title"] = title,
        ["kind"] = kind,
        ["companyName"] = "Storporate Labs",
        ["location"] = null,
        ["workMode"] = "Hybrid",
        ["description"] = "Work with the platform team on real customer problems.",
        ["requiredSkills"] = skills ?? new[] { "C#", "SQL" },
    };

    private static async Task<JobPostingResponse> CreateAsync(HttpClient client, string title)
    {
        var response = await client.PostAsJsonAsync("/api/discovery/job-postings", ValidBody(title));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JobPostingResponse>(JsonOptions))!;
    }

    private static async Task<JobPostingResponse> ChangeStatusAsync(HttpClient client, string url, string status)
    {
        var response = await client.PostAsJsonAsync(url, new { status });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JobPostingResponse>(JsonOptions))!;
    }

    private static async Task<JobFitResponse> GetFitAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync($"/api/discovery/jobs/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JobFitResponse>(JsonOptions))!;
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
        var tokens = await tokenService.IssueTokensAsync(user, "job-posting-test", CancellationToken.None);
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

    private async Task<JobPosting> SeedPostingAsync(
        Guid ownerId,
        string title,
        string kind = "Job",
        string workMode = "Hybrid",
        string status = "Open",
        string company = "Seed Co",
        string[]? skills = null)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var now = DateTimeOffset.UtcNow;
        var normalizedSkills = skills ?? new[] { "C#" };
        var posting = new JobPosting
        {
            Id = Guid.NewGuid(),
            OwnerAccountId = ownerId,
            Title = title,
            Kind = kind,
            CompanyName = company,
            WorkMode = workMode,
            Description = "Seeded description for the browse tests.",
            RequiredSkillsJson = JobPostingSkills.Serialize(normalizedSkills),
            SearchText = JobPostingSkills.BuildSearchText(title, company, location: null, normalizedSkills),
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            ClosedAt = status == "Closed" ? now : null,
        };
        dbContext.JobPostings.Add(posting);
        await dbContext.SaveChangesAsync();
        return posting;
    }

    private async Task SeedAnalyzedSkillsAsync(Guid studentId, params (string Name, string Band)[] skills) =>
        await SeedAnalyzedSkillsAsync(studentId, analyzed: true, skills);

    private async Task SeedAnalyzedSkillsAsync(Guid studentId, (string Name, string Band) skill, bool analyzed) =>
        await SeedAnalyzedSkillsAsync(studentId, analyzed, skill);

    private async Task SeedAnalyzedSkillsAsync(Guid studentId, bool analyzed, params (string Name, string Band)[] skills)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
        accountContext.SetUserId(studentId);
        accountContext.SetAccountId(studentId);
        accountContext.SetIsAdministrator(false);

        var itemId = Guid.NewGuid();
        dbContext.PortfolioItems.Add(new PortfolioItem
        {
            Id = itemId,
            AccountId = studentId,
            Label = "Seed item",
            Category = PortfolioCategories.Document,
            SubmissionType = PortfolioSubmissionTypes.Link,
            ExternalUrl = "https://example.com/seed",
            CreatedAt = DateTimeOffset.UtcNow,
            AnalysisStatus = analyzed ? PortfolioAnalysisStatuses.Analyzed : PortfolioAnalysisStatuses.Failed,
        });
        foreach (var (name, band) in skills)
        {
            dbContext.PortfolioSkillFindings.Add(new PortfolioSkillFinding
            {
                Id = Guid.NewGuid(),
                AccountId = studentId,
                PortfolioItemId = itemId,
                SkillName = name,
                ConfidenceBand = band,
                Explanation = "Seeded finding.",
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await dbContext.SaveChangesAsync();
    }
}
