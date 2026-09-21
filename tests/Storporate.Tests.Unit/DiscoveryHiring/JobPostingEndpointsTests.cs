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
        using var doc = JsonDocument.Parse(raw);
        AssertNoNumbers(doc.RootElement);
        Assert.DoesNotContain("score", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("percent", raw, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertNoNumbers(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number:
                Assert.Fail("The response must not contain any numeric value.");
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AssertNoNumbers(property.Value);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AssertNoNumbers(item);
                }

                break;
        }
    }

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
        var posting = new JobPosting
        {
            Id = Guid.NewGuid(),
            OwnerAccountId = ownerId,
            Title = title,
            Kind = kind,
            CompanyName = company,
            WorkMode = workMode,
            Description = "Seeded description for the browse tests.",
            RequiredSkillsJson = JobPostingSkills.Serialize(skills ?? new[] { "C#" }),
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
