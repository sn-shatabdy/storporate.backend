using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.DiscoveryHiring.Outreach;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.Tests.Unit.Auth;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.DiscoveryHiring;

/// <summary>
/// STOR-68 end-to-end coverage of the employer shortlist (<c>/api/discovery/shortlist</c>), employer
/// outreach (<c>/api/discovery/outreach</c>) and the student inbox (<c>/api/discovery/inbox</c>)
/// through the real pipeline.
/// </summary>
public class OutreachEndpointsTests : IClassFixture<DiscoveryHiringEndpointsFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly DiscoveryHiringEndpointsFactory _factory;

    public OutreachEndpointsTests(DiscoveryHiringEndpointsFactory factory)
    {
        _factory = factory;
    }

    // --------------------------------------------------------------- shortlist

    [Fact]
    public async Task Shortlist_Add_IsIdempotent_Lists_AndRemoves()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var other = await SeedUserAsync(ActorTypes.Student);
        var candidateId = await SeedCandidateAsync(student.Id, "Shortlist Student");
        var otherCandidateId = await SeedCandidateAsync(other.Id, "Other Student");
        using var client = await ClientForAsync(org);

        var first = await client.PostAsJsonAsync("/api/discovery/shortlist", new { candidateId });
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var firstBody = await first.Content.ReadAsStringAsync();
        var created = JsonSerializer.Deserialize<ShortlistEntryResponse>(firstBody, JsonOptions)!;
        Assert.Equal(candidateId, created.CandidateId);
        Assert.Equal("Shortlist Student", created.DisplayName);
        Assert.Equal("Fixture headline", created.Headline);
        Assert.Equal("Fixture University", created.University);
        Assert.Equal("Computer Science", created.FieldOfStudy);
        Assert.Equal(3, created.StudyYear);
        Assert.True(created.Available);
        Assert.Null(created.Conversation);

        var second = await client.PostAsJsonAsync("/api/discovery/shortlist", new { candidateId });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(created.SavedAt, (await second.Content.ReadFromJsonAsync<ShortlistEntryResponse>(JsonOptions))!.SavedAt);

        await client.PostAsJsonAsync("/api/discovery/shortlist", new { candidateId = otherCandidateId });
        var raw = await client.GetStringAsync("/api/discovery/shortlist");
        var list = JsonSerializer.Deserialize<ShortlistListResponse>(raw, JsonOptions)!;
        Assert.Equal(new[] { otherCandidateId, candidateId }, list.Items.Select(i => i.CandidateId).ToArray());
        AssertNoIdentity(raw, student.Id, student.Email);
        AssertNoIdentity(raw, other.Id, other.Email);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/discovery/shortlist/{candidateId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/discovery/shortlist/{candidateId}")).StatusCode);
        var after = await client.GetFromJsonAsync<ShortlistListResponse>("/api/discovery/shortlist", JsonOptions);
        Assert.Equal(otherCandidateId, Assert.Single(after!.Items).CandidateId);
    }

    [Fact]
    public async Task Shortlist_UnknownCandidate_Returns404()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(org);

        var response = await client.PostAsJsonAsync("/api/discovery/shortlist", new { candidateId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("candidate_not_found", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Shortlist_IsPerOrganization_AndUnavailableAfterTheEntryIsRemoved()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var otherOrg = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var candidateId = await SeedCandidateAsync(student.Id, "Opt Out Student");
        using var client = await ClientForAsync(org);
        using var otherClient = await ClientForAsync(otherOrg);

        await client.PostAsJsonAsync("/api/discovery/shortlist", new { candidateId });
        var otherList = await otherClient.GetFromJsonAsync<ShortlistListResponse>("/api/discovery/shortlist", JsonOptions);
        Assert.Empty(otherList!.Items);

        await RemoveCandidateAsync(candidateId);

        var raw = await client.GetStringAsync("/api/discovery/shortlist");
        var item = Assert.Single(JsonSerializer.Deserialize<ShortlistListResponse>(raw, JsonOptions)!.Items);
        Assert.False(item.Available);
        Assert.Equal(candidateId, item.CandidateId);
        Assert.Null(item.DisplayName);
        Assert.Null(item.Headline);
        Assert.Null(item.University);
        Assert.Null(item.FieldOfStudy);
        Assert.Null(item.StudyYear);
        Assert.DoesNotContain("Opt Out Student", raw, StringComparison.Ordinal);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/discovery/shortlist/{candidateId}")).StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<ShortlistListResponse>("/api/discovery/shortlist", JsonOptions))!.Items);
    }

    [Fact]
    public async Task Shortlist_ShowsTheConversationOnceInvited()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var candidateId = await SeedCandidateAsync(student.Id, "Invited Student");
        using var client = await ClientForAsync(org);
        await client.PostAsJsonAsync("/api/discovery/shortlist", new { candidateId });

        var conversation = await InviteAsync(client, candidateId);

        var list = await client.GetFromJsonAsync<ShortlistListResponse>("/api/discovery/shortlist", JsonOptions);
        var row = Assert.Single(list!.Items);
        Assert.Equal(conversation.Id, row.Conversation!.Id);
        Assert.Equal("Invited", row.Conversation.Status);
    }

    // ------------------------------------------------------------------ invite

    [Fact]
    public async Task Invite_CreatesConversationWithFirstMessage_AndBothSidesSeeIt()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var candidateId = await SeedCandidateAsync(student.Id, "Invite Student");
        using var orgClient = await ClientForAsync(org);
        using var studentClient = await ClientForAsync(student);

        var response = await orgClient.PostAsJsonAsync(
            "/api/discovery/outreach",
            new { candidateId, organizationName = "  Acme Ltd  ", message = "  We liked your portfolio.  " });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var raw = await response.Content.ReadAsStringAsync();
        var detail = JsonSerializer.Deserialize<ConversationDetailResponse>(raw, JsonOptions)!;
        Assert.Equal("Invite Student", detail.CounterpartName);
        Assert.Equal("Invited", detail.Status);
        Assert.Equal("We liked your portfolio.", detail.LastMessagePreview);
        var message = Assert.Single(detail.Messages);
        Assert.Equal("Organization", message.SenderRole);
        Assert.Equal("We liked your portfolio.", message.Body);
        Assert.True(message.FromMe);
        AssertNoIdentity(raw, student.Id, student.Email);

        var orgList = await orgClient.GetStringAsync("/api/discovery/outreach");
        var orgSummary = Assert.Single(JsonSerializer.Deserialize<ConversationListResponse>(orgList, JsonOptions)!.Items);
        Assert.Equal(detail.Id, orgSummary.Id);
        Assert.Equal("Invite Student", orgSummary.CounterpartName);
        AssertNoIdentity(orgList, student.Id, student.Email);

        var inboxRaw = await studentClient.GetStringAsync("/api/discovery/inbox");
        var inbox = JsonSerializer.Deserialize<ConversationListResponse>(inboxRaw, JsonOptions)!;
        var summary = Assert.Single(inbox.Items);
        Assert.Equal("Acme Ltd", summary.CounterpartName);
        Assert.Equal("Invited", summary.Status);
        Assert.Equal("We liked your portfolio.", summary.LastMessagePreview);
        AssertNoIdentity(inboxRaw, org.Id, org.Email);

        var studentDetailRaw = await studentClient.GetStringAsync($"/api/discovery/inbox/{detail.Id}");
        var studentDetail = JsonSerializer.Deserialize<ConversationDetailResponse>(studentDetailRaw, JsonOptions)!;
        Assert.False(Assert.Single(studentDetail.Messages).FromMe);
        AssertNoIdentity(studentDetailRaw, org.Id, org.Email);
    }

    [Fact]
    public async Task Invite_PreviewIsTruncatedTo120Characters()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var candidateId = await SeedCandidateAsync(student.Id, "Preview Student");
        using var client = await ClientForAsync(org);

        var detail = await InviteAsync(client, candidateId, message: new string('m', 500));
        Assert.Equal(120, detail.LastMessagePreview.Length);
        Assert.Equal(500, Assert.Single(detail.Messages).Body.Length);
        var summary = Assert.Single((await client.GetFromJsonAsync<ConversationListResponse>("/api/discovery/outreach", JsonOptions))!.Items);
        Assert.Equal(120, summary.LastMessagePreview.Length);
    }

    [Fact]
    public async Task Invite_Twice_Returns409AlreadyStarted()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var candidateId = await SeedCandidateAsync(student.Id, "Duplicate Student");
        using var client = await ClientForAsync(org);
        await InviteAsync(client, candidateId);

        var second = await client.PostAsJsonAsync(
            "/api/discovery/outreach", new { candidateId, organizationName = "Acme", message = "Again" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("outreach_already_started", await ErrorCodeAsync(second));
    }

    [Fact]
    public async Task Invite_UnknownCandidate_Returns404()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        using var client = await ClientForAsync(org);

        var response = await client.PostAsJsonAsync(
            "/api/discovery/outreach", new { candidateId = Guid.NewGuid(), organizationName = "Acme", message = "Hello" });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("candidate_not_found", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Invite_ValidatesOrganizationNameAndMessage()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var candidateId = await SeedCandidateAsync(student.Id, "Validation Student");
        using var client = await ClientForAsync(org);

        foreach (var name in new[] { null, "", " A ", new string('n', 151) })
        {
            var bad = await client.PostAsJsonAsync(
                "/api/discovery/outreach", new { candidateId, organizationName = name, message = "Hello" });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Equal("outreach_organization_name_invalid", await ErrorCodeAsync(bad));
        }

        foreach (var message in new[] { null, "", "    ", new string('x', 2001) })
        {
            var bad = await client.PostAsJsonAsync(
                "/api/discovery/outreach", new { candidateId, organizationName = "Acme", message });
            Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
            Assert.Equal("outreach_message_invalid", await ErrorCodeAsync(bad));
        }

        var atLimit = await client.PostAsJsonAsync(
            "/api/discovery/outreach",
            new { candidateId, organizationName = new string('n', 150), message = new string('x', 2000) });
        Assert.Equal(HttpStatusCode.Created, atLimit.StatusCode);
    }

    // ------------------------------------------------------ conversation rules

    [Fact]
    public async Task Employer_CannotMessageWhileInvited_UntilStudentReplies()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var candidateId = await SeedCandidateAsync(student.Id, "Reply Student");
        using var orgClient = await ClientForAsync(org);
        using var studentClient = await ClientForAsync(student);
        var conversation = await InviteAsync(orgClient, candidateId);
        var messagesUrl = $"/api/discovery/outreach/{conversation.Id}/messages";

        var blocked = await orgClient.PostAsJsonAsync(messagesUrl, new { message = "Hello?" });
        Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
        Assert.Equal("outreach_awaiting_reply", await ErrorCodeAsync(blocked));

        var reply = await studentClient.PostAsJsonAsync(
            $"/api/discovery/inbox/{conversation.Id}/reply", new { message = "  Happy to talk.  " });
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        var replied = (await reply.Content.ReadFromJsonAsync<ConversationDetailResponse>(JsonOptions))!;
        Assert.Equal("Replied", replied.Status);
        Assert.Equal(new[] { "Organization", "Student" }, replied.Messages.Select(m => m.SenderRole).ToArray());
        Assert.Equal(new[] { false, true }, replied.Messages.Select(m => m.FromMe).ToArray());
        Assert.Equal("Happy to talk.", replied.LastMessagePreview);

        var sent = await orgClient.PostAsJsonAsync(messagesUrl, new { message = "Great, let's set up a call." });
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        var afterSend = (await sent.Content.ReadFromJsonAsync<ConversationDetailResponse>(JsonOptions))!;
        Assert.Equal("Replied", afterSend.Status);
        Assert.Equal(3, afterSend.Messages.Count);
        Assert.Equal(new[] { true, false, true }, afterSend.Messages.Select(m => m.FromMe).ToArray());

        var again = await studentClient.PostAsJsonAsync(
            $"/api/discovery/inbox/{conversation.Id}/reply", new { message = "Tuesday works." });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var final = (await again.Content.ReadFromJsonAsync<ConversationDetailResponse>(JsonOptions))!;
        Assert.Equal("Replied", final.Status);
        Assert.Equal(4, final.Messages.Count);

        var invalid = await orgClient.PostAsJsonAsync(messagesUrl, new { message = "  " });
        Assert.Equal("outreach_message_invalid", await ErrorCodeAsync(invalid));
        var invalidReply = await studentClient.PostAsJsonAsync($"/api/discovery/inbox/{conversation.Id}/reply", new { message = "" });
        Assert.Equal("outreach_message_invalid", await ErrorCodeAsync(invalidReply));
    }

    [Fact]
    public async Task Conversations_AreListedMostRecentlyUpdatedFirst()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var s1 = await SeedUserAsync(ActorTypes.Student);
        var s2 = await SeedUserAsync(ActorTypes.Student);
        using var orgClient = await ClientForAsync(org);
        using var s1Client = await ClientForAsync(s1);
        var first = await InviteAsync(orgClient, await SeedCandidateAsync(s1.Id, "First Student"));
        var second = await InviteAsync(orgClient, await SeedCandidateAsync(s2.Id, "Second Student"));

        var before = await orgClient.GetFromJsonAsync<ConversationListResponse>("/api/discovery/outreach", JsonOptions);
        Assert.Equal(new[] { second.Id, first.Id }, before!.Items.Select(i => i.Id).ToArray());

        await s1Client.PostAsJsonAsync($"/api/discovery/inbox/{first.Id}/reply", new { message = "Bumped." });
        var after = await orgClient.GetFromJsonAsync<ConversationListResponse>("/api/discovery/outreach", JsonOptions);
        Assert.Equal(new[] { first.Id, second.Id }, after!.Items.Select(i => i.Id).ToArray());
        Assert.Equal("Bumped.", after.Items[0].LastMessagePreview);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decline_IsFinal_FromInvitedOrReplied(bool repliedFirst)
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var candidateId = await SeedCandidateAsync(student.Id, "Decline Student");
        using var orgClient = await ClientForAsync(org);
        using var studentClient = await ClientForAsync(student);
        var conversation = await InviteAsync(orgClient, candidateId);
        if (repliedFirst)
        {
            await studentClient.PostAsJsonAsync($"/api/discovery/inbox/{conversation.Id}/reply", new { message = "Maybe." });
        }

        var declined = await studentClient.PostAsync($"/api/discovery/inbox/{conversation.Id}/decline", content: null);
        Assert.Equal(HttpStatusCode.OK, declined.StatusCode);
        var body = (await declined.Content.ReadFromJsonAsync<ConversationDetailResponse>(JsonOptions))!;
        Assert.Equal("Declined", body.Status);
        Assert.Equal(repliedFirst ? 2 : 1, body.Messages.Count);

        var again = await studentClient.PostAsync($"/api/discovery/inbox/{conversation.Id}/decline", content: null);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("Declined", (await again.Content.ReadFromJsonAsync<ConversationDetailResponse>(JsonOptions))!.Status);

        var send = await orgClient.PostAsJsonAsync($"/api/discovery/outreach/{conversation.Id}/messages", new { message = "Please?" });
        Assert.Equal(HttpStatusCode.Conflict, send.StatusCode);
        Assert.Equal("outreach_declined", await ErrorCodeAsync(send));

        var reInvite = await orgClient.PostAsJsonAsync(
            "/api/discovery/outreach", new { candidateId, organizationName = "Acme", message = "Second try" });
        Assert.Equal(HttpStatusCode.Conflict, reInvite.StatusCode);
        Assert.Equal("outreach_declined", await ErrorCodeAsync(reInvite));

        var reply = await studentClient.PostAsJsonAsync($"/api/discovery/inbox/{conversation.Id}/reply", new { message = "Changed my mind." });
        Assert.Equal(HttpStatusCode.Conflict, reply.StatusCode);
        Assert.Equal("outreach_declined", await ErrorCodeAsync(reply));

        // Both sides can still read it, with the Declined state visible.
        Assert.Equal("Declined", (await studentClient.GetFromJsonAsync<ConversationDetailResponse>(
            $"/api/discovery/inbox/{conversation.Id}", JsonOptions))!.Status);
        Assert.Equal("Declined", (await orgClient.GetFromJsonAsync<ConversationDetailResponse>(
            $"/api/discovery/outreach/{conversation.Id}", JsonOptions))!.Status);
    }

    [Fact]
    public async Task ExistingConversation_SurvivesTheStudentOptingOut()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var candidateId = await SeedCandidateAsync(student.Id, "Survivor Student");
        using var orgClient = await ClientForAsync(org);
        using var studentClient = await ClientForAsync(student);
        var conversation = await InviteAsync(orgClient, candidateId);

        await RemoveCandidateAsync(candidateId);

        var detail = await orgClient.GetFromJsonAsync<ConversationDetailResponse>($"/api/discovery/outreach/{conversation.Id}", JsonOptions);
        Assert.Equal("Survivor Student", detail!.CounterpartName);
        var reply = await studentClient.PostAsJsonAsync($"/api/discovery/inbox/{conversation.Id}/reply", new { message = "Still here." });
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        var send = await orgClient.PostAsJsonAsync($"/api/discovery/outreach/{conversation.Id}/messages", new { message = "Good to hear." });
        Assert.Equal(HttpStatusCode.Created, send.StatusCode);
    }

    // -------------------------------------------------------- cross-account 404

    [Fact]
    public async Task ForeignAndUnknownConversations_Return404OnBothSides()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var intruderOrg = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var intruderStudent = await SeedUserAsync(ActorTypes.Student);
        var candidateId = await SeedCandidateAsync(student.Id, "Private Student");
        using var orgClient = await ClientForAsync(org);
        var conversation = await InviteAsync(orgClient, candidateId);
        using var intruderOrgClient = await ClientForAsync(intruderOrg);
        using var intruderStudentClient = await ClientForAsync(intruderStudent);

        foreach (var id in new[] { conversation.Id, Guid.NewGuid() })
        {
            var responses = new[]
            {
                await intruderOrgClient.GetAsync($"/api/discovery/outreach/{id}"),
                await intruderOrgClient.PostAsJsonAsync($"/api/discovery/outreach/{id}/messages", new { message = "Hi" }),
                await intruderStudentClient.GetAsync($"/api/discovery/inbox/{id}"),
                await intruderStudentClient.PostAsJsonAsync($"/api/discovery/inbox/{id}/reply", new { message = "Hi" }),
                await intruderStudentClient.PostAsync($"/api/discovery/inbox/{id}/decline", content: null),
            };
            foreach (var response in responses)
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.Equal("outreach_not_found", await ErrorCodeAsync(response));
            }
        }

        Assert.Empty((await intruderOrgClient.GetFromJsonAsync<ConversationListResponse>("/api/discovery/outreach", JsonOptions))!.Items);
        Assert.Empty((await intruderStudentClient.GetFromJsonAsync<ConversationListResponse>("/api/discovery/inbox", JsonOptions))!.Items);

        // The intruding organization's shortlist delete does not touch another organization's entry.
        await orgClient.PostAsJsonAsync("/api/discovery/shortlist", new { candidateId });
        Assert.Equal(HttpStatusCode.NoContent, (await intruderOrgClient.DeleteAsync($"/api/discovery/shortlist/{candidateId}")).StatusCode);
        Assert.Single((await orgClient.GetFromJsonAsync<ShortlistListResponse>("/api/discovery/shortlist", JsonOptions))!.Items);
    }

    // ------------------------------------------------------------- permissions

    [Fact]
    public async Task Authorization_EmployerEndpointsRejectStudent_InboxRejectsOrganization_AnonymousIs401()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var candidateId = await SeedCandidateAsync(student.Id, "Auth Student");
        using var orgClient = await ClientForAsync(org);
        var conversation = await InviteAsync(orgClient, candidateId);
        var id = conversation.Id;

        using var studentClient = await ClientForAsync(student);
        Assert.Equal(HttpStatusCode.Forbidden, (await studentClient.PostAsJsonAsync("/api/discovery/shortlist", new { candidateId })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await studentClient.GetAsync("/api/discovery/shortlist")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await studentClient.DeleteAsync($"/api/discovery/shortlist/{candidateId}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await studentClient.PostAsJsonAsync("/api/discovery/outreach", new { candidateId, organizationName = "Acme", message = "Hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await studentClient.GetAsync("/api/discovery/outreach")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await studentClient.GetAsync($"/api/discovery/outreach/{id}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await studentClient.PostAsJsonAsync($"/api/discovery/outreach/{id}/messages", new { message = "Hi" })).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await orgClient.GetAsync("/api/discovery/inbox")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await orgClient.GetAsync($"/api/discovery/inbox/{id}")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await orgClient.PostAsJsonAsync($"/api/discovery/inbox/{id}/reply", new { message = "Hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await orgClient.PostAsync($"/api/discovery/inbox/{id}/decline", content: null)).StatusCode);

        using var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/discovery/shortlist", new { candidateId })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/discovery/shortlist")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/discovery/outreach")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/discovery/inbox")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/discovery/inbox/{id}/decline", content: null)).StatusCode);
    }

    // ------------------------------------------------------------------- audit

    [Fact]
    public async Task AuditRows_HoldIdsAndStatusOnly()
    {
        var org = await SeedUserAsync(ActorTypes.Organization);
        var student = await SeedUserAsync(ActorTypes.Student);
        var candidateId = await SeedCandidateAsync(student.Id, "Audit Student");
        var audit = _factory.Services.GetRequiredService<FakeAuditLogWriter>();
        using var orgClient = await ClientForAsync(org);
        using var studentClient = await ClientForAsync(student);
        const string OrgSecret = "Audit-Secret-Org-Name";
        const string Body1 = "audit secret invitation body";
        const string Body2 = "audit secret reply body";
        const string Body3 = "audit secret follow-up body";

        await orgClient.PostAsJsonAsync("/api/discovery/shortlist", new { candidateId });
        var invite = await orgClient.PostAsJsonAsync(
            "/api/discovery/outreach", new { candidateId, organizationName = OrgSecret, message = Body1 });
        var conversation = (await invite.Content.ReadFromJsonAsync<ConversationDetailResponse>(JsonOptions))!;
        await studentClient.PostAsJsonAsync($"/api/discovery/inbox/{conversation.Id}/reply", new { message = Body2 });
        await orgClient.PostAsJsonAsync($"/api/discovery/outreach/{conversation.Id}/messages", new { message = Body3 });
        await studentClient.PostAsync($"/api/discovery/inbox/{conversation.Id}/decline", content: null);
        await orgClient.DeleteAsync($"/api/discovery/shortlist/{candidateId}");

        var rows = audit.Recorded
            .Where(r => r.Action.StartsWith("shortlist_", StringComparison.Ordinal)
                || r.Action.StartsWith("outreach_", StringComparison.Ordinal))
            .Where(r => r.MetadataJson!.Contains(conversation.Id.ToString(), StringComparison.Ordinal)
                || r.MetadataJson.Contains(candidateId.ToString(), StringComparison.Ordinal))
            .ToList();

        Assert.Equal(
            new[] { "outreach_declined", "outreach_invited", "outreach_message_sent", "outreach_message_sent", "shortlist_added", "shortlist_removed" },
            rows.Select(r => r.Action).OrderBy(a => a, StringComparer.Ordinal).ToArray());
        var senders = rows.Where(r => r.Action == "outreach_message_sent")
            .Select(r => JsonDocument.Parse(r.MetadataJson!).RootElement.GetProperty("senderRole").GetString())
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(new[] { "Organization", "Student" }, senders);

        foreach (var row in rows)
        {
            var text = row.MetadataJson + "|" + row.ResourceId + "|" + row.ResourceType;
            foreach (var forbidden in new[] { Body1, Body2, Body3, OrgSecret, "Audit Student", student.Email, org.Email, student.Id.ToString(), org.Id.ToString() })
            {
                Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // ----------------------------------------------------------------- helpers

    private static void AssertNoIdentity(string body, Guid accountId, string email)
    {
        Assert.DoesNotContain(accountId.ToString(), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(accountId.ToString("N"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(email, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("email", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accountId", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("storageKey", body, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("errorCode").GetString();
    }

    private static async Task<ConversationDetailResponse> InviteAsync(
        HttpClient client,
        Guid candidateId,
        string organizationName = "Acme Ltd",
        string message = "We would like to talk.")
    {
        var response = await client.PostAsJsonAsync("/api/discovery/outreach", new { candidateId, organizationName, message });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ConversationDetailResponse>(JsonOptions))!;
    }

    private async Task<HttpClient> ClientForAsync(User user)
    {
        using var scope = _factory.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<IJwtTokenService>();
        var tokens = await tokenService.IssueTokensAsync(user, "outreach-test", CancellationToken.None);
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

    private async Task<Guid> SeedCandidateAsync(Guid studentAccountId, string displayName)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        var id = Guid.NewGuid();
        dbContext.TalentIndexEntries.Add(new TalentIndexEntry
        {
            Id = id,
            StudentAccountId = studentAccountId,
            DisplayName = displayName,
            Headline = "Fixture headline",
            University = "Fixture University",
            FieldOfStudy = "Computer Science",
            StudyYear = 3,
            ItemsJson = "[]",
            SearchText = "seed",
            ContentHash = "seed",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await dbContext.SaveChangesAsync();
        return id;
    }

    private async Task RemoveCandidateAsync(Guid candidateId)
    {
        using var scope = _factory.Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<WriteDbContext>();
        dbContext.TalentIndexEntries.RemoveRange(await dbContext.TalentIndexEntries.Where(e => e.Id == candidateId).ToListAsync());
        await dbContext.SaveChangesAsync();
    }
}
