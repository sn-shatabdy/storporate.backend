using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.InstitutionalClubNetwork.SponsorshipRequests;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Auth;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.InstitutionalClubNetwork;

/// <summary>
/// STOR-72 end-to-end coverage of the sponsorship request endpoints (<c>/api/sponsorship/requests</c>)
/// on both sides through the real pipeline with real-issued tokens.
/// </summary>
public class SponsorshipRequestEndpointsTests : IClassFixture<DiscoveryHiringEndpointsFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DiscoveryHiringEndpointsFactory _factory;

    public SponsorshipRequestEndpointsTests(DiscoveryHiringEndpointsFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task HappyPath_Send_View_Message_Accept_Complete_RecordsOutcome()
    {
        var world = await NewWorldAsync();
        var body = ValidBody(world.GoalId, "Annual Hackathon");
        body["eventDate"] = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30).ToString("yyyy-MM-dd");
        body["amountRequested"] = 50_000;

        var post = await world.Club.PostAsJsonAsync("/api/sponsorship/requests", body);
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var created = await ReadAsync(post);
        Assert.Equal("Sent", created.Status);
        Assert.Equal("Annual Hackathon", created.EventTitle);
        Assert.Equal(50_000, created.AmountRequested);
        Assert.Equal(new SponsorshipRequestClubInfo("Tech Club", "Some University"), created.Club);
        Assert.Equal(new SponsorshipRequestCompanyInfo("Acme Ltd", "Brand push"), created.Company);
        Assert.Equal(new[] { "message" }, created.AllowedActions);
        Assert.Empty(created.Messages);
        Assert.Null(created.Outcome);
        var id = created.Id;

        // Club and company lists.
        var sent = (await world.Club.GetFromJsonAsync<SponsorshipRequestListResponse>("/api/sponsorship/requests/sent", JsonOptions))!;
        var summary = Assert.Single(sent.Items);
        Assert.Equal("Acme Ltd", summary.CounterpartName);
        Assert.Equal(0, summary.MessageCount);
        var received = (await world.Company.GetFromJsonAsync<SponsorshipRequestListResponse>("/api/sponsorship/requests/received", JsonOptions))!;
        var receivedSummary = Assert.Single(received.Items);
        Assert.Equal("Tech Club", receivedSummary.CounterpartName);
        Assert.Equal("Sent", receivedSummary.Status);

        // Club opening its own request never marks it Viewed.
        var clubView = await GetAsync(world.Club, $"/api/sponsorship/requests/sent/{id}");
        Assert.Equal("Sent", clubView.Status);
        Assert.Null(clubView.ViewedAt);

        // Company opens: Sent -> Viewed, once.
        var opened = await GetAsync(world.Company, $"/api/sponsorship/requests/received/{id}");
        Assert.Equal("Viewed", opened.Status);
        Assert.NotNull(opened.ViewedAt);
        Assert.Equal(new[] { "message", "accept", "decline" }, opened.AllowedActions);
        var openedAgain = await GetAsync(world.Company, $"/api/sponsorship/requests/received/{id}");
        Assert.Equal(opened.ViewedAt, openedAgain.ViewedAt);
        Assert.Equal(opened.UpdatedAt, openedAgain.UpdatedAt);

        // A message moves Viewed -> InDiscussion.
        var message = await PostAsync(world.Company, $"/api/sponsorship/requests/received/{id}/messages", new { body = "  Can you share the budget split?  " }, HttpStatusCode.Created);
        Assert.Equal("InDiscussion", message.Status);
        var first = Assert.Single(message.Messages);
        Assert.Equal("Company", first.From);
        Assert.Equal("Can you share the budget split?", first.Body);
        var reply = await PostAsync(world.Club, $"/api/sponsorship/requests/sent/{id}/messages", new { body = "Sure, attached below." }, HttpStatusCode.Created);
        Assert.Equal(new[] { "Company", "Club" }, reply.Messages.Select(m => m.From).ToArray());

        // Accept -> Agreed.
        var accepted = await PostAsync(world.Company, $"/api/sponsorship/requests/received/{id}/accept", new { note = "  Happy to help  " });
        Assert.Equal("Agreed", accepted.Status);
        Assert.Equal("Happy to help", accepted.DecisionNote);
        Assert.NotNull(accepted.DecidedAt);
        Assert.Equal(new[] { "message", "complete" }, accepted.AllowedActions);

        // Messages still allowed in Agreed and keep it Agreed.
        var agreedMessage = await PostAsync(world.Club, $"/api/sponsorship/requests/sent/{id}/messages", new { body = "Thank you." }, HttpStatusCode.Created);
        Assert.Equal("Agreed", agreedMessage.Status);

        // Complete from the club side, with the outcome.
        var completed = await PostAsync(world.Club, $"/api/sponsorship/requests/sent/{id}/complete", new { outcomeNote = "  Event ran with 300 attendees  ", agreedAmount = 45_000 });
        Assert.Equal("Completed", completed.Status);
        Assert.Equal(new SponsorshipRequestOutcomeInfo("Event ran with 300 attendees", 45_000), completed.Outcome);
        Assert.NotNull(completed.CompletedAt);
        Assert.Empty(completed.AllowedActions);
        Assert.Equal(3, completed.Messages.Count);

        var companyView = await GetAsync(world.Company, $"/api/sponsorship/requests/received/{id}");
        Assert.Equal("Completed", companyView.Status);
        Assert.Equal(45_000, companyView.Outcome!.AgreedAmount);

        // Stored outcome is what STOR-52 and STOR-55 will read.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var row = await db.SponsorshipRequests.AsNoTracking().SingleAsync(r => r.Id == id);
        Assert.Equal("Event ran with 300 attendees", row.OutcomeNote);
        Assert.Equal(45_000, row.AgreedAmount);
        Assert.Equal(world.ClubUser.Id, row.ClubOwnerAccountId);
        Assert.Equal(world.CompanyUser.Id, row.CompanyOwnerAccountId);
    }

    [Fact]
    public async Task CompanyCanCompleteAnAgreedRequest_AgreedAmountIsOptional()
    {
        var world = await NewWorldAsync();
        var id = (await SendAsync(world, "Charity Run")).Id;
        await PostAsync(world.Company, $"/api/sponsorship/requests/received/{id}/accept", new { });
        var completed = await PostAsync(world.Company, $"/api/sponsorship/requests/received/{id}/complete", new { outcomeNote = "Done", agreedAmount = (int?)null });
        Assert.Equal("Completed", completed.Status);
        Assert.Equal(new SponsorshipRequestOutcomeInfo("Done", null), completed.Outcome);
        Assert.Null(completed.DecisionNote);
    }

    [Fact]
    public async Task DeclinePath_IsFinal_BlocksMessagesAndFurtherTransitions()
    {
        var world = await NewWorldAsync();
        var id = (await SendAsync(world, "Winter Fest")).Id;

        var declined = await PostAsync(world.Company, $"/api/sponsorship/requests/received/{id}/decline", new { reason = " Budget is closed " });
        Assert.Equal("Declined", declined.Status);
        Assert.Equal("Budget is closed", declined.DecisionNote);
        Assert.NotNull(declined.DecidedAt);
        Assert.Empty(declined.AllowedActions);

        foreach (var (client, path) in new[] { (world.Club, $"/api/sponsorship/requests/sent/{id}"), (world.Company, $"/api/sponsorship/requests/received/{id}") })
        {
            var message = await client.PostAsJsonAsync($"{path}/messages", new { body = "hello" });
            Assert.Equal(HttpStatusCode.Conflict, message.StatusCode);
            Assert.Equal("sponsorship_request_closed", await ErrorCodeAsync(message));
        }

        foreach (var action in new[] { "accept", "decline" })
        {
            var response = await world.Company.PostAsJsonAsync($"/api/sponsorship/requests/received/{id}/{action}", new { });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("sponsorship_request_invalid_transition", await ErrorCodeAsync(response));
        }

        var complete = await world.Club.PostAsJsonAsync($"/api/sponsorship/requests/sent/{id}/complete", new { outcomeNote = "x" });
        Assert.Equal(HttpStatusCode.Conflict, complete.StatusCode);
        Assert.Equal("sponsorship_request_invalid_transition", await ErrorCodeAsync(complete));

        // A declined request no longer blocks a new one with the same title.
        var again = await world.Club.PostAsJsonAsync("/api/sponsorship/requests", ValidBody(world.GoalId, "Winter Fest"));
        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [Fact]
    public async Task Transitions_FromWrongStatus_Return409_AndCompletedIsFinal()
    {
        var world = await NewWorldAsync();
        var id = (await SendAsync(world, "Tech Talk")).Id;

        // Complete before Agreed.
        var early = await world.Club.PostAsJsonAsync($"/api/sponsorship/requests/sent/{id}/complete", new { outcomeNote = "x" });
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("sponsorship_request_invalid_transition", await ErrorCodeAsync(early));

        await PostAsync(world.Company, $"/api/sponsorship/requests/received/{id}/accept", new { });
        // Accept or decline once Agreed.
        foreach (var action in new[] { "accept", "decline" })
        {
            var response = await world.Company.PostAsJsonAsync($"/api/sponsorship/requests/received/{id}/{action}", new { });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        await PostAsync(world.Club, $"/api/sponsorship/requests/sent/{id}/complete", new { outcomeNote = "Done" });
        var second = await world.Company.PostAsJsonAsync($"/api/sponsorship/requests/received/{id}/complete", new { outcomeNote = "Again" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        var late = await world.Club.PostAsJsonAsync($"/api/sponsorship/requests/sent/{id}/messages", new { body = "hi" });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("sponsorship_request_closed", await ErrorCodeAsync(late));
    }

    [Fact]
    public async Task ClubMessageOnSent_MovesToInDiscussion_WithoutCompanyOpeningIt()
    {
        var world = await NewWorldAsync();
        var id = (await SendAsync(world, "Quiz Night")).Id;
        var result = await PostAsync(world.Club, $"/api/sponsorship/requests/sent/{id}/messages", new { body = "Any questions?" }, HttpStatusCode.Created);
        Assert.Equal("InDiscussion", result.Status);
        Assert.Null(result.ViewedAt);

        // The company can still accept or decline from InDiscussion.
        var view = await GetAsync(world.Company, $"/api/sponsorship/requests/received/{id}");
        Assert.Equal("InDiscussion", view.Status);
        Assert.Equal(new[] { "message", "accept", "decline" }, view.AllowedActions);
    }

    [Fact]
    public async Task Messages_ThreadCapAt200_Returns409()
    {
        var world = await NewWorldAsync();
        var id = (await SendAsync(world, "Long Thread")).Id;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
            for (var i = 0; i < SponsorshipRequestRules.MaxMessages; i++)
            {
                db.SponsorshipRequestMessages.Add(new SponsorshipRequestMessage
                {
                    Id = Guid.NewGuid(),
                    RequestId = id,
                    SenderSide = i % 2 == 0 ? "Club" : "Company",
                    Body = $"m{i}",
                    CreatedAt = DateTimeOffset.UtcNow.AddSeconds(i),
                });
            }

            await db.SaveChangesAsync();
        }

        var response = await world.Club.PostAsJsonAsync($"/api/sponsorship/requests/sent/{id}/messages", new { body = "one too many" });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("sponsorship_request_thread_full", await ErrorCodeAsync(response));
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("TOO_LONG", false)]
    [InlineData("ok", true)]
    public async Task MessageBody_ValidatedOnBothSides(string text, bool valid)
    {
        var world = await NewWorldAsync();
        var id = (await SendAsync(world, $"Msg {Guid.NewGuid():N}")).Id;
        text = text == "TOO_LONG" ? new string('m', 2001) : text;
        foreach (var (client, path) in new[] { (world.Club, "sent"), (world.Company, "received") })
        {
            var response = await client.PostAsJsonAsync($"/api/sponsorship/requests/{path}/{id}/messages", new { body = text });
            if (valid)
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            }
            else
            {
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("sponsorship_request_message_invalid", await ErrorCodeAsync(response));
            }
        }
    }

    [Fact]
    public async Task Send_WithoutProfile_Is404_DraftProfileIs409()
    {
        var goalId = await SeedGoalAsync(await SeedUserAsync(ActorTypes.Organization), "Acme Ltd", "Goal", SponsorshipGoalStatuses.Active);

        using var noProfile = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        var missing = await noProfile.PostAsJsonAsync("/api/sponsorship/requests", ValidBody(goalId, "Event"));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("club_profile_not_found", await ErrorCodeAsync(missing));

        var draftOwner = await SeedUserAsync(ActorTypes.Club);
        await SeedProfileAsync(draftOwner, "Draft Club", "Uni", ClubProfileStatuses.Draft);
        using var draft = await ClientForAsync(draftOwner);
        var conflict = await draft.PostAsJsonAsync("/api/sponsorship/requests", ValidBody(goalId, "Event"));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("club_profile_not_published", await ErrorCodeAsync(conflict));
    }

    [Fact]
    public async Task Send_PausedOrUnknownGoal_Is404()
    {
        var world = await NewWorldAsync();
        var pausedGoal = await SeedGoalAsync(await SeedUserAsync(ActorTypes.Organization), "Paused Co", "Paused goal", SponsorshipGoalStatuses.Paused);
        foreach (var goalId in new[] { pausedGoal, Guid.NewGuid() })
        {
            var response = await world.Club.PostAsJsonAsync("/api/sponsorship/requests", ValidBody(goalId, "Event"));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("sponsorship_goal_not_found", await ErrorCodeAsync(response));
        }
    }

    [Fact]
    public async Task Send_Duplicate_IsCaseInsensitiveAndOnlyForNonFinalRequests()
    {
        var world = await NewWorldAsync();
        var first = await SendAsync(world, "Robotics Expo");

        var duplicate = await world.Club.PostAsJsonAsync("/api/sponsorship/requests", ValidBody(world.GoalId, "  robotics EXPO "));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("sponsorship_request_duplicate", await ErrorCodeAsync(duplicate));

        // A different title, or the same title for another goal set, is fine.
        Assert.Equal(HttpStatusCode.Created, (await world.Club.PostAsJsonAsync("/api/sponsorship/requests", ValidBody(world.GoalId, "Robotics Expo 2"))).StatusCode);
        var otherGoal = await SeedGoalAsync(await SeedUserAsync(ActorTypes.Organization), "Other Co", "Other goal", SponsorshipGoalStatuses.Active);
        Assert.Equal(HttpStatusCode.Created, (await world.Club.PostAsJsonAsync("/api/sponsorship/requests", ValidBody(otherGoal, "Robotics Expo"))).StatusCode);

        // Still blocked while Agreed, allowed again once Completed.
        await PostAsync(world.Company, $"/api/sponsorship/requests/received/{first.Id}/accept", new { });
        Assert.Equal(HttpStatusCode.Conflict, (await world.Club.PostAsJsonAsync("/api/sponsorship/requests", ValidBody(world.GoalId, "Robotics Expo"))).StatusCode);
        await PostAsync(world.Club, $"/api/sponsorship/requests/sent/{first.Id}/complete", new { outcomeNote = "Done" });
        Assert.Equal(HttpStatusCode.Created, (await world.Club.PostAsJsonAsync("/api/sponsorship/requests", ValidBody(world.GoalId, "Robotics Expo"))).StatusCode);
    }

    [Fact]
    public async Task CrossAccount_ForeignRequestsAre404OnBothSides()
    {
        var world = await NewWorldAsync();
        var id = (await SendAsync(world, "Private Event")).Id;
        using var otherClub = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        using var otherCompany = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));

        var probes = new List<HttpResponseMessage>
        {
            await otherClub.GetAsync($"/api/sponsorship/requests/sent/{id}"),
            await otherClub.PostAsJsonAsync($"/api/sponsorship/requests/sent/{id}/messages", new { body = "hi" }),
            await otherClub.PostAsJsonAsync($"/api/sponsorship/requests/sent/{id}/complete", new { outcomeNote = "x" }),
            await otherCompany.GetAsync($"/api/sponsorship/requests/received/{id}"),
            await otherCompany.PostAsJsonAsync($"/api/sponsorship/requests/received/{id}/messages", new { body = "hi" }),
            await otherCompany.PostAsJsonAsync($"/api/sponsorship/requests/received/{id}/accept", new { }),
            await otherCompany.PostAsJsonAsync($"/api/sponsorship/requests/received/{id}/decline", new { }),
            await otherCompany.PostAsJsonAsync($"/api/sponsorship/requests/received/{id}/complete", new { outcomeNote = "x" }),
            // Each side's own id space: the club cannot use the company routes' data and vice versa.
            await world.Club.GetAsync($"/api/sponsorship/requests/sent/{Guid.NewGuid()}"),
        };
        foreach (var response in probes)
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("sponsorship_request_not_found", await ErrorCodeAsync(response));
        }

        // Foreign lists are empty, and the request stayed untouched (not marked Viewed).
        Assert.Empty((await otherClub.GetFromJsonAsync<SponsorshipRequestListResponse>("/api/sponsorship/requests/sent", JsonOptions))!.Items);
        Assert.Empty((await otherCompany.GetFromJsonAsync<SponsorshipRequestListResponse>("/api/sponsorship/requests/received", JsonOptions))!.Items);
        Assert.Equal("Sent", (await GetAsync(world.Club, $"/api/sponsorship/requests/sent/{id}")).Status);
    }

    [Fact]
    public async Task List_StatusFilter_NewestUpdatedFirst_InvalidStatusIs400()
    {
        var world = await NewWorldAsync();
        var a = await SendAsync(world, "Event A");
        await Task.Delay(10);
        var b = await SendAsync(world, "Event B");
        await Task.Delay(10);
        await PostAsync(world.Company, $"/api/sponsorship/requests/received/{a.Id}/accept", new { });

        var all = (await world.Club.GetFromJsonAsync<SponsorshipRequestListResponse>("/api/sponsorship/requests/sent", JsonOptions))!;
        Assert.Equal(new[] { a.Id, b.Id }, all.Items.Select(i => i.Id).ToArray());

        var agreed = (await world.Company.GetFromJsonAsync<SponsorshipRequestListResponse>("/api/sponsorship/requests/received?status=Agreed", JsonOptions))!;
        Assert.Equal(new[] { a.Id }, agreed.Items.Select(i => i.Id).ToArray());
        var sent = (await world.Club.GetFromJsonAsync<SponsorshipRequestListResponse>("/api/sponsorship/requests/sent?status=Sent", JsonOptions))!;
        Assert.Equal(new[] { b.Id }, sent.Items.Select(i => i.Id).ToArray());
        Assert.Empty((await world.Club.GetFromJsonAsync<SponsorshipRequestListResponse>("/api/sponsorship/requests/sent?status=Completed", JsonOptions))!.Items);

        foreach (var path in new[] { "sent", "received" })
        {
            var client = path == "sent" ? world.Club : world.Company;
            foreach (var status in new[] { "Nope", "agreed" })
            {
                var response = await client.GetAsync($"/api/sponsorship/requests/{path}?status={status}");
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
                Assert.Equal("sponsorship_request_status_invalid", await ErrorCodeAsync(response));
            }
        }

        // Message count in summaries.
        await PostAsync(world.Club, $"/api/sponsorship/requests/sent/{b.Id}/messages", new { body = "one" }, HttpStatusCode.Created);
        var counted = (await world.Club.GetFromJsonAsync<SponsorshipRequestListResponse>("/api/sponsorship/requests/sent", JsonOptions))!;
        Assert.Equal(1, counted.Items.Single(i => i.Id == b.Id).MessageCount);
    }

    [Fact]
    public async Task GoalSetDelete_LeavesRequestsIntactWithNullGoalAndSnapshotNames()
    {
        var world = await NewWorldAsync();
        var created = await SendAsync(world, "Survives Delete");

        var delete = await world.Company.DeleteAsync($"/api/sponsorship/goals/{world.GoalId}");
        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
            var row = await db.SponsorshipRequests.AsNoTracking().SingleAsync(r => r.Id == created.Id);
            Assert.Null(row.GoalSetId);
            Assert.Equal("Brand push", row.GoalName);
            Assert.Equal("Acme Ltd", row.CompanyName);
        }

        var club = await GetAsync(world.Club, $"/api/sponsorship/requests/sent/{created.Id}");
        Assert.Equal(new SponsorshipRequestCompanyInfo("Acme Ltd", "Brand push"), club.Company);
        var company = await GetAsync(world.Company, $"/api/sponsorship/requests/received/{created.Id}");
        Assert.Equal("Viewed", company.Status);
        Assert.Equal("Brand push", (await world.Company.GetFromJsonAsync<SponsorshipRequestListResponse>("/api/sponsorship/requests/received", JsonOptions))!.Items.Single().GoalName);
    }

    [Theory]
    [InlineData("goalId", null, "sponsorship_request_goal_required")]
    [InlineData("eventTitle", "A", "sponsorship_request_event_title_invalid")]
    [InlineData("eventTitle", null, "sponsorship_request_event_title_invalid")]
    [InlineData("eventTitle", "LONG_TITLE", "sponsorship_request_event_title_invalid")]
    [InlineData("eventDate", "PAST", "sponsorship_request_event_date_invalid")]
    [InlineData("eventDescription", "too short", "sponsorship_request_event_description_invalid")]
    [InlineData("eventDescription", "LONG_DESCRIPTION", "sponsorship_request_event_description_invalid")]
    [InlineData("ask", "short", "sponsorship_request_ask_invalid")]
    [InlineData("ask", "LONG_ASK", "sponsorship_request_ask_invalid")]
    [InlineData("offer", "short", "sponsorship_request_offer_invalid")]
    [InlineData("offer", "LONG_OFFER", "sponsorship_request_offer_invalid")]
    [InlineData("amountRequested", "NEGATIVE", "sponsorship_request_amount_invalid")]
    [InlineData("amountRequested", "HUGE", "sponsorship_request_amount_invalid")]
    public async Task Send_InvalidFields_Return400WithErrorCode(string field, string? value, string expectedCode)
    {
        var world = await NewWorldAsync();
        var body = ValidBody(world.GoalId, "Valid event");
        body[field] = value switch
        {
            "LONG_TITLE" => new string('t', 151),
            "LONG_DESCRIPTION" => new string('d', 3001),
            "LONG_ASK" => new string('a', 1501),
            "LONG_OFFER" => new string('o', 1501),
            "PAST" => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-3).ToString("yyyy-MM-dd"),
            "NEGATIVE" => -1,
            "HUGE" => 1_000_000_001,
            _ => value,
        };
        var response = await world.Club.PostAsJsonAsync("/api/sponsorship/requests", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedCode, await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Send_EventDateYesterdayAndOptionalFieldsOmitted_AreAccepted()
    {
        var world = await NewWorldAsync();
        var body = ValidBody(world.GoalId, "Yesterday Event");
        body["eventDate"] = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1).ToString("yyyy-MM-dd");
        var response = await world.Club.PostAsJsonAsync("/api/sponsorship/requests", body);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var minimal = ValidBody(world.GoalId, "No Date Event");
        minimal.Remove("eventDate");
        minimal.Remove("amountRequested");
        var created = await ReadAsync(await world.Club.PostAsJsonAsync("/api/sponsorship/requests", minimal));
        Assert.Null(created.EventDate);
        Assert.Null(created.AmountRequested);
        using var doc = JsonDocument.Parse(await world.Club.GetStringAsync($"/api/sponsorship/requests/sent/{created.Id}"));
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("eventDate").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("decisionNote").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("outcome").ValueKind);
    }

    [Fact]
    public async Task DecisionAndCompleteBodies_AreValidated()
    {
        var world = await NewWorldAsync();
        var id = (await SendAsync(world, "Validation Event")).Id;

        var longNote = await world.Company.PostAsJsonAsync($"/api/sponsorship/requests/received/{id}/accept", new { note = new string('n', 1001) });
        Assert.Equal("sponsorship_request_note_invalid", await ErrorCodeAsync(longNote));
        var longReason = await world.Company.PostAsJsonAsync($"/api/sponsorship/requests/received/{id}/decline", new { reason = new string('n', 1001) });
        Assert.Equal("sponsorship_request_reason_invalid", await ErrorCodeAsync(longReason));

        await PostAsync(world.Company, $"/api/sponsorship/requests/received/{id}/accept", new { });
        foreach (var body in new object[]
        {
            new { outcomeNote = "" },
            new { outcomeNote = "   " },
            new { outcomeNote = new string('o', 1001) },
        })
        {
            var response = await world.Club.PostAsJsonAsync($"/api/sponsorship/requests/sent/{id}/complete", body);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("sponsorship_request_outcome_note_invalid", await ErrorCodeAsync(response));
        }

        foreach (var amount in new[] { -1, 1_000_000_001 })
        {
            var response = await world.Club.PostAsJsonAsync($"/api/sponsorship/requests/sent/{id}/complete", new { outcomeNote = "ok", agreedAmount = amount });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("sponsorship_request_agreed_amount_invalid", await ErrorCodeAsync(response));
        }

        Assert.Equal("Agreed", (await GetAsync(world.Club, $"/api/sponsorship/requests/sent/{id}")).Status);
        var boundary = await PostAsync(world.Club, $"/api/sponsorship/requests/sent/{id}/complete", new { outcomeNote = "ok", agreedAmount = 1_000_000_000 });
        Assert.Equal(1_000_000_000, boundary.Outcome!.AgreedAmount);
    }

    [Fact]
    public async Task Authorization_WrongActorTypesAndAnonymous()
    {
        var id = Guid.NewGuid();
        using var student = await ClientForAsync(await SeedUserAsync(ActorTypes.Student));
        using var org = await ClientForAsync(await SeedUserAsync(ActorTypes.Organization));
        using var club = await ClientForAsync(await SeedUserAsync(ActorTypes.Club));
        using var anonymous = _factory.CreateClient();

        var clubRequests = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Post, "/api/sponsorship/requests", ValidBody(id, "x")),
            (HttpMethod.Get, "/api/sponsorship/requests/sent", null),
            (HttpMethod.Get, $"/api/sponsorship/requests/sent/{id}", null),
            (HttpMethod.Post, $"/api/sponsorship/requests/sent/{id}/messages", new { body = "x" }),
            (HttpMethod.Post, $"/api/sponsorship/requests/sent/{id}/complete", new { outcomeNote = "x" }),
        };
        var companyRequests = new (HttpMethod Method, string Path, object? Body)[]
        {
            (HttpMethod.Get, "/api/sponsorship/requests/received", null),
            (HttpMethod.Get, $"/api/sponsorship/requests/received/{id}", null),
            (HttpMethod.Post, $"/api/sponsorship/requests/received/{id}/messages", new { body = "x" }),
            (HttpMethod.Post, $"/api/sponsorship/requests/received/{id}/accept", new { }),
            (HttpMethod.Post, $"/api/sponsorship/requests/received/{id}/decline", new { }),
            (HttpMethod.Post, $"/api/sponsorship/requests/received/{id}/complete", new { outcomeNote = "x" }),
        };

        foreach (var caller in new[] { student, org })
        {
            foreach (var r in clubRequests)
            {
                Assert.Equal(HttpStatusCode.Forbidden, (await SendRawAsync(caller, r)).StatusCode);
            }
        }

        foreach (var caller in new[] { student, club })
        {
            foreach (var r in companyRequests)
            {
                Assert.Equal(HttpStatusCode.Forbidden, (await SendRawAsync(caller, r)).StatusCode);
            }
        }

        foreach (var r in clubRequests.Concat(companyRequests))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendRawAsync(anonymous, r)).StatusCode);
        }
    }

    [Fact]
    public async Task Responses_NeverContainOwnerAccountIdsOrEmails()
    {
        var world = await NewWorldAsync();
        var created = await SendAsync(world, "Leak Check");
        var texts = new List<string>();
        texts.Add(await world.Club.GetStringAsync("/api/sponsorship/requests/sent"));
        texts.Add(await world.Club.GetStringAsync($"/api/sponsorship/requests/sent/{created.Id}"));
        texts.Add(await world.Company.GetStringAsync("/api/sponsorship/requests/received"));
        texts.Add(await world.Company.GetStringAsync($"/api/sponsorship/requests/received/{created.Id}"));
        texts.Add(await (await world.Company.PostAsJsonAsync($"/api/sponsorship/requests/received/{created.Id}/messages", new { body = "hi" })).Content.ReadAsStringAsync());
        texts.Add(await (await world.Company.PostAsJsonAsync($"/api/sponsorship/requests/received/{created.Id}/accept", new { })).Content.ReadAsStringAsync());
        texts.Add(await (await world.Club.PostAsJsonAsync($"/api/sponsorship/requests/sent/{created.Id}/complete", new { outcomeNote = "done" })).Content.ReadAsStringAsync());

        foreach (var text in texts)
        {
            Assert.DoesNotContain(world.ClubUser.Id.ToString(), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(world.CompanyUser.Id.ToString(), text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(world.ClubUser.Email, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(world.CompanyUser.Email, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ownerAccountId", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("email", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Audit_RowsContainOnlyIdsSideAndStatus()
    {
        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();
        var world = await NewWorldAsync();
        var body = ValidBody(world.GoalId, "Secret Event Title");
        body["ask"] = "Confidential ask text here";
        body["offer"] = "Confidential offer text here";
        var created = await ReadAsync(await world.Club.PostAsJsonAsync("/api/sponsorship/requests", body));
        var id = created.Id;
        await GetAsync(world.Company, $"/api/sponsorship/requests/received/{id}");
        await GetAsync(world.Company, $"/api/sponsorship/requests/received/{id}");
        await PostAsync(world.Company, $"/api/sponsorship/requests/received/{id}/messages", new { body = "Secret message body" }, HttpStatusCode.Created);
        await PostAsync(world.Company, $"/api/sponsorship/requests/received/{id}/accept", new { note = "Secret decision note" });
        await PostAsync(world.Club, $"/api/sponsorship/requests/sent/{id}/complete", new { outcomeNote = "Secret outcome text" });

        var rows = audit.Recorded.Where(r => r.ResourceId == id.ToString()).ToList();
        Assert.Equal(
            new[]
            {
                "sponsorship_request_sent",
                "sponsorship_request_viewed",
                "sponsorship_request_message_sent",
                "sponsorship_request_status_changed",
                "sponsorship_request_status_changed",
                "sponsorship_request_completed",
            },
            rows.Select(r => r.Action).ToArray());

        foreach (var row in rows)
        {
            Assert.Equal("SponsorshipRequest", row.ResourceType);
            using var meta = JsonDocument.Parse(row.MetadataJson!);
            var props = meta.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
            Assert.Equal(id.ToString(), props["requestId"]);
            Assert.Contains("requestId", props.Keys);
            Assert.All(props.Keys, key => Assert.Contains(key, new[] { "requestId", "goalId", "status", "side" }));
            Assert.DoesNotContain("Secret", row.MetadataJson, StringComparison.Ordinal);
            Assert.DoesNotContain("Confidential", row.MetadataJson, StringComparison.Ordinal);
            Assert.DoesNotContain(world.ClubUser.Email, row.MetadataJson, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(world.CompanyUser.Email, row.MetadataJson, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(world.GoalId.ToString(), JsonDocument.Parse(rows[0].MetadataJson!).RootElement.GetProperty("goalId").GetString());
        Assert.Equal("Company", JsonDocument.Parse(rows[2].MetadataJson!).RootElement.GetProperty("side").GetString());
        Assert.Equal("InDiscussion", JsonDocument.Parse(rows[3].MetadataJson!).RootElement.GetProperty("status").GetString());
        Assert.Equal("Agreed", JsonDocument.Parse(rows[4].MetadataJson!).RootElement.GetProperty("status").GetString());
    }

    private static Dictionary<string, object?> ValidBody(Guid goalId, string title) => new()
    {
        ["goalId"] = goalId,
        ["eventTitle"] = title,
        ["eventDate"] = null,
        ["eventDescription"] = "A day-long event for students to build and show projects.",
        ["ask"] = "Funding for prizes and the venue.",
        ["amountRequested"] = 10_000,
        ["offer"] = "Logo on all material and a stage slot.",
    };

    private static async Task<SponsorshipRequestDetail> ReadAsync(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SponsorshipRequestDetail>(JsonOptions))!;
    }

    private static async Task<SponsorshipRequestDetail> GetAsync(HttpClient client, string path) =>
        await ReadAsync(await client.GetAsync(path));

    private static async Task<SponsorshipRequestDetail> PostAsync(HttpClient client, string path, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await client.PostAsJsonAsync(path, body);
        Assert.Equal(expected, response.StatusCode);
        return await ReadAsync(response);
    }

    private static Task<HttpResponseMessage> SendRawAsync(HttpClient client, (HttpMethod Method, string Path, object? Body) request)
    {
        var message = new HttpRequestMessage(request.Method, request.Path);
        if (request.Body is not null)
        {
            message.Content = JsonContent.Create(request.Body);
        }

        return client.SendAsync(message);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("errorCode").GetString();
    }

    private async Task<SponsorshipRequestDetail> SendAsync(World world, string title) =>
        await ReadAsync(await world.Club.PostAsJsonAsync("/api/sponsorship/requests", ValidBody(world.GoalId, title)));

    private async Task<World> NewWorldAsync()
    {
        var clubUser = await SeedUserAsync(ActorTypes.Club);
        var orgUser = await SeedUserAsync(ActorTypes.Organization);
        await SeedProfileAsync(clubUser, "Tech Club", "Some University", ClubProfileStatuses.Published);
        var goalId = await SeedGoalAsync(orgUser, "Acme Ltd", "Brand push", SponsorshipGoalStatuses.Active);
        return new World(
            clubUser,
            orgUser,
            await ClientForAsync(clubUser),
            await ClientForAsync(orgUser),
            goalId);
    }

    private async Task<HttpClient> ClientForAsync(User user)
    {
        using var scope = _factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();
        var tokens = await tokenService.IssueTokensAsync(user, "sponsorship-request-test", CancellationToken.None);
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

    private async Task SeedProfileAsync(User owner, string name, string university, string status)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var now = DateTimeOffset.UtcNow;
        dbContext.ClubProfiles.Add(new ClubProfile
        {
            Id = Guid.NewGuid(),
            OwnerAccountId = owner.Id,
            Name = name,
            About = "A student club that meets every week.",
            University = university,
            MemberCount = 100,
            AudienceFieldsOfStudy = "[]",
            AudienceYears = "[1]",
            EventsJson = "[]",
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            PublishedAt = status == ClubProfileStatuses.Published ? now : null,
        });
        await dbContext.SaveChangesAsync();
    }

    private async Task<Guid> SeedGoalAsync(User owner, string companyName, string goalName, string status)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var now = DateTimeOffset.UtcNow;
        var goal = new SponsorshipGoalSet
        {
            Id = Guid.NewGuid(),
            OwnerAccountId = owner.Id,
            Name = goalName,
            CompanyName = companyName,
            Objectives = "[\"Brand awareness\"]",
            AudienceFieldsOfStudy = "[]",
            AudienceYears = "[]",
            AudienceCities = "[]",
            AudienceUniversities = "[]",
            EventKinds = "[\"Hackathon\"]",
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
        };
        dbContext.SponsorshipGoalSets.Add(goal);
        await dbContext.SaveChangesAsync();
        return goal.Id;
    }

    private sealed record World(User ClubUser, User CompanyUser, HttpClient Club, HttpClient Company, Guid GoalId);
}
