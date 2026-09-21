using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.DiscoveryHiring.JobApplications;
using Storporate.Modules.DiscoveryHiring.JobPostings;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Auth;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.DiscoveryHiring;

/// <summary>
/// STOR-67 end-to-end coverage of student applications (<c>/api/discovery/jobs/{id}/applications</c>,
/// <c>/api/discovery/applications</c>) and employer applicant review
/// (<c>/api/discovery/job-postings/{id}/applications</c>) through the real pipeline.
/// </summary>
public class JobApplicationEndpointsTests : IClassFixture<DiscoveryHiringEndpointsFactory>
{
    private const string SecretDescription = "Secret item description that must never reach an employer.";
    private const string SecretFileName = "confidential-cv-final.pdf";
    private const string SecretStorageKey = "storage/secret-key-9f8e7d";
    private const string SecretUrl = "https://example.com/private-original";
    private const string SecretReason = "Secret reason the analyzer gave for this skill.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DiscoveryHiringEndpointsFactory _factory;

    public JobApplicationEndpointsTests(DiscoveryHiringEndpointsFactory factory)
    {
        _factory = factory;
    }

    // ------------------------------------------------------------------- apply

    [Fact]
    public async Task Apply_WithProfile_BuildsSnapshotFromProfileAndQualifyingItemsOnly()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        await SeedProfileAsync(student.Id, "Profile Name", headline: "Hidden headline", university: "Dhaka University",
            fieldOfStudy: "Physics", studyYear: 3, showHeadline: false, showFieldOfStudy: false);
        await SeedItemAsync(student.Id, "Analyzed with skills", PortfolioAnalysisStatuses.Analyzed,
            ("Python", "Strong"), ("SQL", "Developing"), ("Docker", "Missing"));
        await SeedItemAsync(student.Id, "Only missing", PortfolioAnalysisStatuses.Analyzed, ("Rust", "Missing"));
        await SeedItemAsync(student.Id, "Not analyzed", PortfolioAnalysisStatuses.Failed, ("Go", "Strong"));
        var posting = await SeedPostingAsync(org.Id, "Snapshot Role", skills: new[] { "python", "Kubernetes" });

        using var studentClient = await ClientForAsync(student);
        var applied = await studentClient.PostAsJsonAsync(
            $"/api/discovery/jobs/{posting.Id}/applications", new { displayName = "Ignored Body Name" });
        Assert.Equal(HttpStatusCode.Created, applied.StatusCode);
        var response = await applied.Content.ReadFromJsonAsync<ApplicationResponse>(JsonOptions);
        Assert.Equal("Submitted", response!.Status);
        Assert.Equal(posting.Id, response.JobPostingId);
        Assert.Equal("Snapshot Role", response.JobTitle);
        Assert.Equal("Seed Co", response.CompanyName);
        Assert.Equal("Job", response.Kind);
        Assert.Equal("Good match", response.FitLabel);

        using var orgClient = await ClientForAsync(org);
        var raw = await orgClient.GetStringAsync($"/api/discovery/job-postings/{posting.Id}/applications/{response.Id}");
        var applicant = JsonSerializer.Deserialize<ApplicantResponse>(raw, JsonOptions)!;

        Assert.Equal("Profile Name", applicant.DisplayName);
        Assert.Null(applicant.Headline);
        Assert.Equal("Dhaka University", applicant.University);
        Assert.Null(applicant.FieldOfStudy);
        Assert.Equal(3, applicant.StudyYear);

        var item = Assert.Single(applicant.Items);
        Assert.Equal("Analyzed with skills", item.Label);
        Assert.Equal(PortfolioCategories.Document, item.Category);
        Assert.Equal(
            new[] { ("Python", "Strong"), ("SQL", "Developing") },
            item.Skills.Select(s => (s.Name, s.Band)).ToArray());

        Assert.Equal("Good match", applicant.Fit.Label);
        Assert.Equal("python", Assert.Single(applicant.Fit.Matched).Name);
        Assert.Equal("Strong", applicant.Fit.Matched[0].Band);
        Assert.Equal(new[] { "Kubernetes" }, applicant.Fit.Missing);

        AssertNoLeaks(raw, student.Email);
        AssertNoLeaks(await applied.Content.ReadAsStringAsync(), student.Email);
        Assert.DoesNotContain("Hidden headline", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Physics", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Only missing", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("Not analyzed", raw, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_WithoutProfile_UsesBodyNameAndLeavesProfileFieldsNull()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var posting = await SeedPostingAsync(org.Id, "No Profile Role");
        using var studentClient = await ClientForAsync(student);

        var applied = await studentClient.PostAsJsonAsync(
            $"/api/discovery/jobs/{posting.Id}/applications", new { displayName = "  Body Name  " });
        Assert.Equal(HttpStatusCode.Created, applied.StatusCode);
        var response = await applied.Content.ReadFromJsonAsync<ApplicationResponse>(JsonOptions);
        Assert.Equal("Not yet", response!.FitLabel);

        using var orgClient = await ClientForAsync(org);
        var applicant = await GetApplicantAsync(orgClient, posting.Id, response.Id);
        Assert.Equal("Body Name", applicant.DisplayName);
        Assert.Null(applicant.Headline);
        Assert.Null(applicant.University);
        Assert.Null(applicant.FieldOfStudy);
        Assert.Null(applicant.StudyYear);
        Assert.Empty(applicant.Items);
    }

    [Fact]
    public async Task Apply_WithoutProfileAndWithoutName_Returns400Required_AndBadNameReturns400Invalid()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var posting = await SeedPostingAsync(org.Id, "Name Validation");
        using var client = await ClientForAsync(student);
        var url = $"/api/discovery/jobs/{posting.Id}/applications";

        var noBody = await client.PostAsync(url, content: null);
        Assert.Equal(HttpStatusCode.BadRequest, noBody.StatusCode);
        Assert.Equal("application_display_name_required", await ErrorCodeAsync(noBody));

        var blank = await client.PostAsJsonAsync(url, new { displayName = "   " });
        Assert.Equal("application_display_name_required", await ErrorCodeAsync(blank));

        var tooShort = await client.PostAsJsonAsync(url, new { displayName = "A" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Equal("application_display_name_invalid", await ErrorCodeAsync(tooShort));

        var tooLong = await client.PostAsJsonAsync(url, new { displayName = new string('x', 81) });
        Assert.Equal("application_display_name_invalid", await ErrorCodeAsync(tooLong));
    }

    [Fact]
    public async Task Apply_Twice_Returns409()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var posting = await SeedPostingAsync(org.Id, "Duplicate Role");
        using var client = await ClientForAsync(student);
        var url = $"/api/discovery/jobs/{posting.Id}/applications";

        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(url, new { displayName = "Dup Student" })).StatusCode);
        var second = await client.PostAsJsonAsync(url, new { displayName = "Dup Student" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("application_already_submitted", await ErrorCodeAsync(second));
    }

    [Fact]
    public async Task Apply_ToPausedClosedOrUnknownPosting_Returns404()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var paused = await SeedPostingAsync(org.Id, "Paused", status: "Paused");
        var closed = await SeedPostingAsync(org.Id, "Closed", status: "Closed");
        using var client = await ClientForAsync(student);

        foreach (var id in new[] { paused.Id, closed.Id, Guid.NewGuid() })
        {
            var response = await client.PostAsJsonAsync($"/api/discovery/jobs/{id}/applications", new { displayName = "Late Student" });
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("job_posting_not_found", await ErrorCodeAsync(response));
        }
    }

    [Fact]
    public async Task ListOwn_ReturnsOnlyCallersApplications_NewestFirst()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var other = await SeedUserAsync(ActorTypes.Student);
        var first = await SeedPostingAsync(org.Id, "First Applied", kind: "Internship");
        var second = await SeedPostingAsync(org.Id, "Second Applied");
        using var client = await ClientForAsync(student);
        using var otherClient = await ClientForAsync(other);

        await client.PostAsJsonAsync($"/api/discovery/jobs/{first.Id}/applications", new { displayName = "List Student" });
        await client.PostAsJsonAsync($"/api/discovery/jobs/{second.Id}/applications", new { displayName = "List Student" });
        await otherClient.PostAsJsonAsync($"/api/discovery/jobs/{first.Id}/applications", new { displayName = "Other Student" });

        var list = await client.GetFromJsonAsync<ApplicationListResponse>("/api/discovery/applications", JsonOptions);
        Assert.Equal(new[] { second.Id, first.Id }, list!.Items.Select(i => i.JobPostingId).ToArray());
        Assert.Equal("Internship", list.Items[1].Kind);
        Assert.All(list.Items, i => Assert.Equal("Submitted", i.Status));

        var otherList = await otherClient.GetFromJsonAsync<ApplicationListResponse>("/api/discovery/applications", JsonOptions);
        Assert.Equal(first.Id, Assert.Single(otherList!.Items).JobPostingId);
    }

    [Fact]
    public async Task JobFitResponse_CarriesTheCallersApplication()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var other = await SeedUserAsync(ActorTypes.Student);
        var applied = await SeedPostingAsync(org.Id, "Fit Applied", skills: new[] { "fitapp-" + Guid.NewGuid().ToString("N")[..6] });
        var untouched = await SeedPostingAsync(org.Id, "Fit Untouched");
        using var client = await ClientForAsync(student);
        using var otherClient = await ClientForAsync(other);

        var created = await (await client.PostAsJsonAsync(
            $"/api/discovery/jobs/{applied.Id}/applications", new { displayName = "Fit Student" }))
            .Content.ReadFromJsonAsync<ApplicationResponse>(JsonOptions);

        var single = await client.GetFromJsonAsync<JobFitResponse>($"/api/discovery/jobs/{applied.Id}", JsonOptions);
        Assert.Equal(created!.Id, single!.Application!.Id);
        Assert.Equal("Submitted", single.Application.Status);

        var noApplication = await client.GetFromJsonAsync<JobFitResponse>($"/api/discovery/jobs/{untouched.Id}", JsonOptions);
        Assert.Null(noApplication!.Application);

        var otherView = await otherClient.GetFromJsonAsync<JobFitResponse>($"/api/discovery/jobs/{applied.Id}", JsonOptions);
        Assert.Null(otherView!.Application);

        var raw = await client.GetStringAsync($"/api/discovery/jobs?q={Uri.EscapeDataString(applied.Title)}");
        var list = JsonSerializer.Deserialize<JobFitListResponse>(raw, JsonOptions)!;
        var row = list.Items.Single(i => i.Id == applied.Id);
        Assert.Equal(created.Id, row.Application!.Id);
        Assert.Contains("\"application\":{\"id\":", raw, StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- employer

    [Fact]
    public async Task EmployerList_ReturnsApplicantsNewestFirst_AndOtherOwnersGet404()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var intruder = await SeedUserAsync(ActorTypes.Organization);
        var s1 = await SeedUserAsync(ActorTypes.Student);
        var s2 = await SeedUserAsync(ActorTypes.Student);
        var posting = await SeedPostingAsync(org.Id, "Employer List");
        var otherPosting = await SeedPostingAsync(org.Id, "Other Posting");
        var a1 = await ApplyAsync(s1, posting.Id, "First Applicant");
        var a2 = await ApplyAsync(s2, posting.Id, "Second Applicant");
        var elsewhere = await ApplyAsync(s1, otherPosting.Id, "First Applicant");

        using var orgClient = await ClientForAsync(org);
        var list = await orgClient.GetFromJsonAsync<ApplicantListResponse>(
            $"/api/discovery/job-postings/{posting.Id}/applications", JsonOptions);
        Assert.Equal(new[] { a2.Id, a1.Id }, list!.Items.Select(i => i.Id).ToArray());
        Assert.Equal("Second Applicant", list.Items[0].DisplayName);

        using var intruderClient = await ClientForAsync(intruder);
        var crossList = await intruderClient.GetAsync($"/api/discovery/job-postings/{posting.Id}/applications");
        var crossGet = await intruderClient.GetAsync($"/api/discovery/job-postings/{posting.Id}/applications/{a1.Id}");
        var crossStatus = await intruderClient.PostAsJsonAsync(
            $"/api/discovery/job-postings/{posting.Id}/applications/{a1.Id}/status", new { status = "Shortlisted" });
        foreach (var response in new[] { crossList, crossGet, crossStatus })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("job_posting_not_found", await ErrorCodeAsync(response));
        }

        // An application id that belongs to a different posting (or is unknown) is application_not_found.
        var mismatched = await orgClient.GetAsync($"/api/discovery/job-postings/{posting.Id}/applications/{elsewhere.Id}");
        var unknown = await orgClient.GetAsync($"/api/discovery/job-postings/{posting.Id}/applications/{Guid.NewGuid()}");
        foreach (var response in new[] { mismatched, unknown })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application_not_found", await ErrorCodeAsync(response));
        }

        // Nothing changed by the intruder.
        Assert.Equal("Submitted", (await GetStatusFromDbAsync(a1.Id)));
    }

    [Fact]
    public async Task EmployerGet_MarksSubmittedAsViewedOnce_WithAuditRow()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var posting = await SeedPostingAsync(org.Id, "Viewed Role");
        var application = await ApplyAsync(student, posting.Id, "Viewed Student");
        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();
        using var orgClient = await ClientForAsync(org);

        var beforeList = await orgClient.GetFromJsonAsync<ApplicantListResponse>(
            $"/api/discovery/job-postings/{posting.Id}/applications", JsonOptions);
        Assert.Equal("Submitted", beforeList!.Items.Single().Status); // listing does not mark viewed

        var first = await GetApplicantAsync(orgClient, posting.Id, application.Id);
        Assert.Equal("Viewed", first.Status);
        Assert.True(first.StatusChangedAt >= first.CreatedAt);
        await GetApplicantAsync(orgClient, posting.Id, application.Id);

        var rows = audit.Recorded.Where(r => r.ResourceId == application.Id.ToString()).ToList();
        Assert.Equal(new[] { "job_application_submitted", "job_application_status_changed" }, rows.Select(r => r.Action).ToArray());
        Assert.Contains("\"status\":\"Viewed\"", rows[1].MetadataJson!, StringComparison.Ordinal);
        Assert.Contains($"\"jobApplicationId\":\"{application.Id}\"", rows[1].MetadataJson!, StringComparison.Ordinal);
        Assert.Contains($"\"jobPostingId\":\"{posting.Id}\"", rows[0].MetadataJson!, StringComparison.Ordinal);

        using var studentClient = await ClientForAsync(student);
        var studentList = await studentClient.GetFromJsonAsync<ApplicationListResponse>("/api/discovery/applications", JsonOptions);
        Assert.Equal("Viewed", studentList!.Items.Single().Status);
    }

    [Fact]
    public async Task EmployerStatusChange_AllowsShortlistAndDeclineInEitherDirection_AndRejectsManualViewed()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var posting = await SeedPostingAsync(org.Id, "Status Role");
        var application = await ApplyAsync(student, posting.Id, "Status Student");
        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();
        using var orgClient = await ClientForAsync(org);
        var url = $"/api/discovery/job-postings/{posting.Id}/applications/{application.Id}/status";

        var shortlisted = await ChangeAsync(orgClient, url, "Shortlisted");
        Assert.Equal("Shortlisted", shortlisted.Status);
        var changedAt = shortlisted.StatusChangedAt;
        Assert.Equal(changedAt, (await ChangeAsync(orgClient, url, "Shortlisted")).StatusChangedAt); // no-op
        Assert.Equal("NotSelected", (await ChangeAsync(orgClient, url, "NotSelected")).Status);
        Assert.Equal("Shortlisted", (await ChangeAsync(orgClient, url, "Shortlisted")).Status);

        // Reading a decided application does not reset it to Viewed.
        Assert.Equal("Shortlisted", (await GetApplicantAsync(orgClient, posting.Id, application.Id)).Status);

        foreach (var bad in new[] { "Submitted", "Viewed", "Open", "" })
        {
            var response = await orgClient.PostAsJsonAsync(url, new { status = bad });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application_status_invalid", await ErrorCodeAsync(response));
        }

        var missing = await orgClient.PostAsJsonAsync(url, new { });
        Assert.Equal("application_status_invalid", await ErrorCodeAsync(missing));

        var statusRows = audit.Recorded
            .Where(r => r.ResourceId == application.Id.ToString() && r.Action == "job_application_status_changed")
            .Select(r => r.MetadataJson!)
            .ToList();
        Assert.Equal(3, statusRows.Count);
        Assert.Contains("\"status\":\"NotSelected\"", statusRows[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task PausedAndClosedPostings_KeepApplicationsReadable_ButRefuseNewOnes()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var late = await SeedUserAsync(ActorTypes.Student);
        var posting = await SeedPostingAsync(org.Id, "Later Closed");
        var application = await ApplyAsync(student, posting.Id, "Early Student");
        using var orgClient = await ClientForAsync(org);

        await orgClient.PostAsJsonAsync($"/api/discovery/job-postings/{posting.Id}/status", new { status = "Closed" });

        var list = await orgClient.GetFromJsonAsync<ApplicantListResponse>(
            $"/api/discovery/job-postings/{posting.Id}/applications", JsonOptions);
        Assert.Equal(application.Id, Assert.Single(list!.Items).Id);
        Assert.Equal("Viewed", (await GetApplicantAsync(orgClient, posting.Id, application.Id)).Status);
        Assert.Equal("Shortlisted", (await ChangeAsync(
            orgClient, $"/api/discovery/job-postings/{posting.Id}/applications/{application.Id}/status", "Shortlisted")).Status);

        using var lateClient = await ClientForAsync(late);
        var refused = await lateClient.PostAsJsonAsync(
            $"/api/discovery/jobs/{posting.Id}/applications", new { displayName = "Late Student" });
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Equal("job_posting_not_found", await ErrorCodeAsync(refused));
    }

    // ------------------------------------------------------------ authorization

    [Fact]
    public async Task Authorization_StudentEndpointsRejectOrganization_EmployerEndpointsRejectStudent_AnonymousIs401()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var posting = await SeedPostingAsync(org.Id, "Auth Role");
        var application = await ApplyAsync(student, posting.Id, "Auth Student");
        var applicantsUrl = $"/api/discovery/job-postings/{posting.Id}/applications";
        var applyUrl = $"/api/discovery/jobs/{posting.Id}/applications";

        using var orgClient = await ClientForAsync(org);
        Assert.Equal(HttpStatusCode.Forbidden, (await orgClient.PostAsJsonAsync(applyUrl, new { displayName = "Org Person" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await orgClient.GetAsync("/api/discovery/applications")).StatusCode);

        using var studentClient = await ClientForAsync(student);
        Assert.Equal(HttpStatusCode.Forbidden, (await studentClient.GetAsync(applicantsUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await studentClient.GetAsync($"{applicantsUrl}/{application.Id}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await studentClient.PostAsJsonAsync($"{applicantsUrl}/{application.Id}/status", new { status = "Shortlisted" })).StatusCode);

        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(applyUrl, new { displayName = "Anon Person" })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/discovery/applications")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(applicantsUrl)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"{applicantsUrl}/{application.Id}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Unauthorized,
            (await anonymous.PostAsJsonAsync($"{applicantsUrl}/{application.Id}/status", new { status = "Shortlisted" })).StatusCode);
    }

    [Fact]
    public async Task Responses_AndAuditRows_NeverCarryPrivateData()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        await SeedProfileAsync(student.Id, "Leak Check", headline: "Builder", university: "BUET", fieldOfStudy: "CSE", studyYear: 2);
        await SeedItemAsync(student.Id, "Leak item", PortfolioAnalysisStatuses.Analyzed, ("Python", "Strong"));
        var posting = await SeedPostingAsync(org.Id, "Leak Role", skills: new[] { "Python", "Go" });
        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();

        var application = await ApplyAsync(student, posting.Id, "Leak Check");
        using var orgClient = await ClientForAsync(org);
        var bodies = new List<string>
        {
            await orgClient.GetStringAsync($"/api/discovery/job-postings/{posting.Id}/applications"),
            await orgClient.GetStringAsync($"/api/discovery/job-postings/{posting.Id}/applications/{application.Id}"),
            await (await orgClient.PostAsJsonAsync(
                $"/api/discovery/job-postings/{posting.Id}/applications/{application.Id}/status",
                new { status = "Shortlisted" })).Content.ReadAsStringAsync(),
        };
        using var studentClient = await ClientForAsync(student);
        bodies.Add(await studentClient.GetStringAsync("/api/discovery/applications"));
        bodies.AddRange(audit.Recorded
            .Where(r => r.ResourceId == application.Id.ToString())
            .Select(r => r.MetadataJson!));

        foreach (var body in bodies)
        {
            AssertNoLeaks(body, student.Email);
            Assert.DoesNotContain(student.Id.ToString(), body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("score", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ----------------------------------------------------------------- helpers

    private static void AssertNoLeaks(string body, string email)
    {
        foreach (var secret in new[] { email, SecretDescription, SecretFileName, SecretStorageKey, SecretUrl, SecretReason })
        {
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("storageKey", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fileName", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("explanation", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("reason", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("description", body, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<ApplicationResponse> ApplyAsync(User student, Guid postingId, string displayName)
    {
        using var client = await ClientForAsync(student);
        var response = await client.PostAsJsonAsync($"/api/discovery/jobs/{postingId}/applications", new { displayName });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApplicationResponse>(JsonOptions))!;
    }

    private static async Task<ApplicantResponse> GetApplicantAsync(HttpClient client, Guid postingId, Guid applicationId)
    {
        var response = await client.GetAsync($"/api/discovery/job-postings/{postingId}/applications/{applicationId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApplicantResponse>(JsonOptions))!;
    }

    private static async Task<ApplicantResponse> ChangeAsync(HttpClient client, string url, string status)
    {
        var response = await client.PostAsJsonAsync(url, new { status });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ApplicantResponse>(JsonOptions))!;
    }

    private async Task<string> GetStatusFromDbAsync(Guid applicationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        return (await db.JobApplications.AsNoTracking().SingleAsync(a => a.Id == applicationId)).Status;
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
        var tokens = await tokenService.IssueTokensAsync(user, "job-application-test", CancellationToken.None);
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
        string status = "Open",
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
            CompanyName = "Seed Co",
            WorkMode = "Hybrid",
            Description = "Seeded description for the application tests.",
            RequiredSkillsJson = JobPostingSkills.Serialize(normalizedSkills),
            SearchText = JobPostingSkills.BuildSearchText(title, "Seed Co", location: null, normalizedSkills),
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            ClosedAt = status == "Closed" ? now : null,
        };
        dbContext.JobPostings.Add(posting);
        await dbContext.SaveChangesAsync();
        return posting;
    }

    private static void SetStudentScope(IServiceScope scope, Guid studentId)
    {
        var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
        accountContext.SetUserId(studentId);
        accountContext.SetAccountId(studentId);
        accountContext.SetIsAdministrator(false);
    }

    private async Task SeedProfileAsync(
        Guid studentId,
        string displayName,
        string? headline = null,
        string? university = null,
        string? fieldOfStudy = null,
        int? studyYear = null,
        bool showHeadline = true,
        bool showUniversity = true,
        bool showFieldOfStudy = true,
        bool showStudyYear = true)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        SetStudentScope(scope, studentId);
        dbContext.StudentSearchProfiles.Add(new StudentSearchProfile
        {
            Id = Guid.NewGuid(),
            AccountId = studentId,
            IsSearchable = false,
            DisplayName = displayName,
            Headline = headline,
            University = university,
            FieldOfStudy = fieldOfStudy,
            StudyYear = studyYear,
            ShowHeadline = showHeadline,
            ShowUniversity = showUniversity,
            ShowFieldOfStudy = showFieldOfStudy,
            ShowStudyYear = showStudyYear,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
    }

    private async Task SeedItemAsync(
        Guid studentId,
        string label,
        string analysisStatus,
        params (string Name, string Band)[] skills)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        SetStudentScope(scope, studentId);

        var itemId = Guid.NewGuid();
        dbContext.PortfolioItems.Add(new PortfolioItem
        {
            Id = itemId,
            AccountId = studentId,
            Label = label,
            Category = PortfolioCategories.Document,
            SubmissionType = PortfolioSubmissionTypes.Link,
            ExternalUrl = SecretUrl,
            StorageKey = SecretStorageKey,
            OriginalFileName = SecretFileName,
            Description = SecretDescription,
            ShareOriginalWithEmployers = true,
            CreatedAt = DateTimeOffset.UtcNow,
            AnalysisStatus = analysisStatus,
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
                Explanation = SecretReason,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await dbContext.SaveChangesAsync();
    }
}
