using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Portfolio;
using Storporate.Modules.Portfolio.Analysis;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Auth;

namespace Storporate.Tests.Unit.Portfolio;

/// <summary>
/// End-to-end integration coverage for STOR-38 Phase 3's two portfolio-analysis endpoints
/// (<c>GET /api/portfolio/items/{id}/analysis</c> and
/// <c>POST /api/portfolio/items/{id}/analysis/retry</c>). Goes through the real
/// ASP.NET Core pipeline with a real-issued access token so the plan's acceptance
/// criteria (200 with empty/populated <c>skills</c>, 404 for cross-account ids, 202 on a
/// successful retry, 409 on a non-retryable status, 403 for a non-Student caller) are
/// exercised against the production handlers — not a hand-rolled duplicate.
/// </summary>
/// <remarks>
/// <para>
/// <b>Tenant isolation.</b> Cross-account isolation is verified against the EF Core global
/// query filter installed by <see cref="WriteDbContext.OnModelCreating"/> for every
/// <see cref="IAccountScoped"/> entity. The test seeds an item owned by a second account
/// while the request runs under the first account's bearer token and asserts the
/// endpoint returns 404 — the same precedent <c>PortfolioEndpointIntegrationTests</c>'s
/// delete-path tests already establish.
/// </para>
/// <para>
/// <b>Why re-establish the ambient context after each request.</b> <c>TestServer</c>
/// terminates the request <see cref="System.Threading.ExecutionContext"/> when the
/// response is sent, so the AsyncLocal-backed <see cref="AmbientAccountContext"/> values
/// the middleware wrote are gone by the time the test scope's <see cref="WriteDbContext"/>
/// resolves. We re-<c>SetUserId</c> / <c>SetAccountId</c> for the test thread before any
/// post-request <c>DbContext</c> query — same pattern the existing
/// <c>PostPortfolioItem_LinkSubmission_Returns201AndStoresOnlyUrl</c> test uses for its
/// post-insert read.
/// </para>
/// </remarks>
public class PortfolioAnalysisEndpointIntegrationTests : IClassFixture<AuthEndpointsFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AuthEndpointsFactory _factory;

    public PortfolioAnalysisEndpointIntegrationTests(AuthEndpointsFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetAnalysis_CallersOwnItem_Returns200WithEmptySkillsArray_WhileNotAnalyzed()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);

        var item = await SeedPortfolioItemAsync(
            user.Id,
            PortfolioAnalysisStatuses.NotAnalyzed,
            lastAnalyzedAt: null);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync($"/api/portfolio/items/{item.Id}/analysis");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GetPortfolioItemAnalysisResponse>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(PortfolioAnalysisStatuses.NotAnalyzed, payload!.Status);
        Assert.Null(payload.LastAnalyzedAt);
        Assert.Null(payload.ErrorMessage);
        Assert.NotNull(payload.Skills); // empty array, never null
        Assert.Empty(payload.Skills);
    }

    [Fact]
    public async Task GetAnalysis_CallersOwnItem_Returns200WithPopulatedSkillsArray_OnceAnalyzed()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);

        var analyzedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var item = await SeedPortfolioItemAsync(
            user.Id,
            PortfolioAnalysisStatuses.Analyzed,
            lastAnalyzedAt: analyzedAt);

        await SeedSkillFindingsAsync(
            accountId: user.Id,
            portfolioItemId: item.Id,
            findings:
            [
                ("React", ConfidenceBands.Strong, "Builds a component-based UI in the description."),
                ("Public Speaking", ConfidenceBands.Developing, "Mentions a class presentation but no transcript."),
            ]);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync($"/api/portfolio/items/{item.Id}/analysis");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GetPortfolioItemAnalysisResponse>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(PortfolioAnalysisStatuses.Analyzed, payload!.Status);
        Assert.NotNull(payload.LastAnalyzedAt);
        Assert.Equal(analyzedAt, payload.LastAnalyzedAt);
        Assert.Null(payload.ErrorMessage);
        Assert.Equal(2, payload.Skills.Count);
        Assert.Contains(payload.Skills, s => s.SkillName == "React" && s.ConfidenceBand == ConfidenceBands.Strong);
        Assert.Contains(payload.Skills, s => s.SkillName == "Public Speaking" && s.ConfidenceBand == ConfidenceBands.Developing);
    }

    [Fact]
    public async Task GetAnalysis_CallersOwnFailedItem_Returns200WithErrorMessage()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);

        var item = await SeedPortfolioItemAsync(
            user.Id,
            PortfolioAnalysisStatuses.Failed,
            lastAnalyzedAt: null);
        await SeedFailedJobAsync(
            accountId: user.Id,
            portfolioItemId: item.Id,
            errorMessage: "LLM provider error: provider unreachable");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync($"/api/portfolio/items/{item.Id}/analysis");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var payload = await response.Content.ReadFromJsonAsync<GetPortfolioItemAnalysisResponse>(JsonOptions);
        Assert.NotNull(payload);
        Assert.Equal(PortfolioAnalysisStatuses.Failed, payload!.Status);
        Assert.Equal("LLM provider error: provider unreachable", payload.ErrorMessage);
        Assert.Empty(payload.Skills);
    }

    [Fact]
    public async Task GetAnalysis_OtherAccountsItem_Returns404()
    {
        var owner = await SeedStudentUserAsync();
        var caller = await SeedStudentUserAsync();
        var callerTokens = await IssueTokensAsync(caller);

        var item = await SeedPortfolioItemAsync(
            owner.Id,
            PortfolioAnalysisStatuses.Analyzed,
            lastAnalyzedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", callerTokens.AccessToken);

        var response = await client.GetAsync($"/api/portfolio/items/{item.Id}/analysis");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetAnalysis_UnknownItemId_Returns404()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync($"/api/portfolio/items/{Guid.NewGuid()}/analysis");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task GetAnalysis_WithoutBearerToken_Returns401()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync($"/api/portfolio/items/{Guid.NewGuid()}/analysis");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetAnalysis_NonStudentCaller_Returns403()
    {
        var caller = await SeedUserAsync(ActorTypes.Organization, "non-student-get@example.com");
        var tokens = await IssueTokensAsync(caller);

        var item = await SeedPortfolioItemAsync(
            caller.Id,
            PortfolioAnalysisStatuses.NotAnalyzed,
            lastAnalyzedAt: null);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.GetAsync($"/api/portfolio/items/{item.Id}/analysis");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PostRetry_FailedItem_Returns202_ResetsStatusAndEnqueuesPendingJob()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);

        var item = await SeedPortfolioItemAsync(
            user.Id,
            PortfolioAnalysisStatuses.Failed,
            lastAnalyzedAt: null);
        await SeedFailedJobAsync(
            accountId: user.Id,
            portfolioItemId: item.Id,
            errorMessage: "previous failure");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsync($"/api/portfolio/items/{item.Id}/analysis/retry", content: null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body);
        Assert.Equal(item.Id, json.RootElement.GetProperty("portfolioItemId").GetGuid());
        var newJobId = json.RootElement.GetProperty("newJobId").GetGuid();
        Assert.NotEqual(Guid.Empty, newJobId);

        // Re-establish the ambient context so the post-assertion DbContext read
        // is unfiltered against the caller's account (TestServer drops the
        // AsyncLocal at response time — same caveat as the create-path test).
        ReestablishAmbientContext(user.Id);

        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var reloadedItem = await dbContext.PortfolioItems.AsNoTracking().SingleAsync(i => i.Id == item.Id);
        Assert.Equal(PortfolioAnalysisStatuses.NotAnalyzed, reloadedItem.AnalysisStatus);
        Assert.Null(reloadedItem.LastAnalyzedAt);

        var newJob = await dbContext.Jobs.AsNoTracking().SingleAsync(j => j.Id == newJobId);
        Assert.Equal(PortfolioJobTypes.AnalyzePortfolioItem, newJob.Type);
        Assert.Equal(JobStatus.Pending, newJob.Status);
        Assert.Equal(0, newJob.AttemptCount);
        Assert.Equal(user.Id, newJob.AccountId);

        var payload = System.Text.Json.JsonSerializer.Deserialize<AnalyzePortfolioItemPayload>(newJob.PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal(item.Id, payload!.PortfolioItemId);
    }

    [Theory]
    [InlineData("Analyzing")]
    [InlineData("Analyzed")]
    [InlineData("Unsupported")]
    [InlineData("NotAnalyzed")]
    public async Task PostRetry_NonFailedStatus_Returns409WithExplanatoryMessage(string nonRetryableStatus)
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);

        var item = await SeedPortfolioItemAsync(
            user.Id,
            nonRetryableStatus,
            lastAnalyzedAt: nonRetryableStatus == PortfolioAnalysisStatuses.Analyzed ? DateTimeOffset.UtcNow.AddMinutes(-5) : null);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsync($"/api/portfolio/items/{item.Id}/analysis/retry", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var errorCode = await ReadErrorCodeAsync(response);
        Assert.Equal("portfolio_analysis_not_retryable", errorCode);

        var message = await ReadErrorMessageAsync(response);
        Assert.NotNull(message);
        Assert.NotEmpty(message);
    }

    [Fact]
    public async Task PostRetry_OtherAccountsItem_Returns404()
    {
        var owner = await SeedStudentUserAsync();
        var caller = await SeedStudentUserAsync();
        var callerTokens = await IssueTokensAsync(caller);

        var item = await SeedPortfolioItemAsync(
            owner.Id,
            PortfolioAnalysisStatuses.Failed,
            lastAnalyzedAt: null);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", callerTokens.AccessToken);

        var response = await client.PostAsync($"/api/portfolio/items/{item.Id}/analysis/retry", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostRetry_UnknownItemId_Returns404()
    {
        var user = await SeedStudentUserAsync();
        var tokens = await IssueTokensAsync(user);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsync($"/api/portfolio/items/{Guid.NewGuid()}/analysis/retry", content: null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostRetry_WithoutBearerToken_Returns401()
    {
        using var client = _factory.CreateClient();

        var response = await client.PostAsync($"/api/portfolio/items/{Guid.NewGuid()}/analysis/retry", content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PostRetry_NonStudentCaller_Returns403()
    {
        var caller = await SeedUserAsync(ActorTypes.Organization, "non-student-retry@example.com");
        var tokens = await IssueTokensAsync(caller);

        var item = await SeedPortfolioItemAsync(
            caller.Id,
            PortfolioAnalysisStatuses.Failed,
            lastAnalyzedAt: null);

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        var response = await client.PostAsync($"/api/portfolio/items/{item.Id}/analysis/retry", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ----- Test infrastructure below this line -----

    private async Task<User> SeedStudentUserAsync() =>
        await SeedUserAsync(ActorTypes.Student, $"portfolio-analysis-test-{Guid.NewGuid():N}@example.com");

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
        return await tokenService.IssueTokensAsync(user, "portfolio-analysis-test", CancellationToken.None);
    }

    /// <summary>Seeds a <see cref="PortfolioItem"/> directly via the in-memory
    /// <see cref="WriteDbContext"/> so a test can stage the exact <c>AnalysisStatus</c>
    /// (and <c>LastAnalyzedAt</c>) it needs without going through the create endpoint.
    /// The <see cref="WriteDbContext"/> resolves through the production DI graph, so
    /// the global query filter and interceptor run exactly as they do in production.</summary>
    private async Task<PortfolioItem> SeedPortfolioItemAsync(
        Guid accountId,
        string analysisStatus,
        DateTimeOffset? lastAnalyzedAt)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();

        // The RowLevelSecurityInterceptor rejects IAccountScoped writes when no
        // ambient account is set. Stage the owner account as the ambient caller
        // so the seed passes the same gate a real request would — and so the
        // seed is itself cross-account-safe (a test that seeds with the wrong
        // account id fails loudly rather than silently writing under the wrong
        // tenancy).
        var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
        accountContext.SetUserId(accountId);
        accountContext.SetAccountId(accountId);
        accountContext.SetIsAdministrator(false);

        var item = new PortfolioItem
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Label = "Item",
            Category = PortfolioCategories.Document,
            SubmissionType = PortfolioSubmissionTypes.Link,
            ExternalUrl = "https://example.com/x",
            CreatedAt = DateTimeOffset.UtcNow,
            AnalysisStatus = analysisStatus,
            LastAnalyzedAt = lastAnalyzedAt,
        };
        dbContext.PortfolioItems.Add(item);
        await dbContext.SaveChangesAsync();
        return item;
    }

    /// <summary>Seeds one or more <see cref="PortfolioSkillFinding"/> rows for the
    /// given item. Used by the <c>Analyzed</c> read test to populate the response
    /// payload the endpoint surfaces.</summary>
    private async Task SeedSkillFindingsAsync(
        Guid accountId,
        Guid portfolioItemId,
        IReadOnlyList<(string SkillName, string ConfidenceBand, string Explanation)> findings)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();

        var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
        accountContext.SetUserId(accountId);
        accountContext.SetAccountId(accountId);
        accountContext.SetIsAdministrator(false);

        var now = DateTimeOffset.UtcNow;
        foreach (var finding in findings)
        {
            dbContext.PortfolioSkillFindings.Add(new PortfolioSkillFinding
            {
                Id = Guid.NewGuid(),
                AccountId = accountId,
                PortfolioItemId = portfolioItemId,
                SkillName = finding.SkillName,
                ConfidenceBand = finding.ConfidenceBand,
                Explanation = finding.Explanation,
                CreatedAt = now,
            });
        }
        await dbContext.SaveChangesAsync();
    }

    /// <summary>Seeds a <see cref="JobStatus.Failed"/> job for the given item — the
    /// shape the read endpoint looks up via payload deserialization to surface the
    /// error message on a <see cref="PortfolioAnalysisStatuses.Failed"/> item.</summary>
    private async Task SeedFailedJobAsync(
        Guid accountId,
        Guid portfolioItemId,
        string errorMessage)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();

        var accountContext = scope.ServiceProvider.GetRequiredService<IAccountContextWriter>();
        accountContext.SetUserId(accountId);
        accountContext.SetAccountId(accountId);
        accountContext.SetIsAdministrator(false);

        var now = DateTime.UtcNow;
        dbContext.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(),
            Type = PortfolioJobTypes.AnalyzePortfolioItem,
            Status = JobStatus.Failed,
            AttemptCount = PortfolioAnalysisJobProcessor.MaxAttempts,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(
                new AnalyzePortfolioItemPayload(portfolioItemId)),
            ErrorMessage = errorMessage,
            AccountId = accountId,
            CreatedAt = now.AddMinutes(-10),
            UpdatedAt = now.AddMinutes(-5),
            StartedAt = now.AddMinutes(-9),
            CompletedAt = now.AddMinutes(-5),
        });
        await dbContext.SaveChangesAsync();
    }

    /// <summary>Re-establishes the ambient <see cref="IAccountContextWriter"/> for
    /// the test thread so a post-request DbContext read is unfiltered against the
    /// caller's account — TestServer drops the AsyncLocal at response time.</summary>
    private void ReestablishAmbientContext(Guid userId)
    {
        var accountContext = _factory.Services.GetRequiredService<IAccountContextWriter>();
        accountContext.SetUserId(userId);
        accountContext.SetAccountId(userId);
        accountContext.SetIsAdministrator(false);
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var json = JsonDocument.Parse(body);
        if (json.RootElement.TryGetProperty("errorCode", out var errorCode))
        {
            return errorCode.GetString();
        }
        return null;
    }

    private static async Task<string?> ReadErrorMessageAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var json = JsonDocument.Parse(body);
        if (json.RootElement.TryGetProperty("message", out var message))
        {
            return message.GetString();
        }
        return null;
    }
}
