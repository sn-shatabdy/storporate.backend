using Storporate.Modules.InstitutionalClubNetwork.SponsorshipRequests;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.InstitutionalClubNetwork;

/// <summary>STOR-72 pure state-rule coverage.</summary>
public class SponsorshipRequestRulesTests
{
    private const string Club = SponsorshipRequestSides.Club;
    private const string Company = SponsorshipRequestSides.Company;

    [Theory]
    [InlineData("Sent", false)]
    [InlineData("Viewed", false)]
    [InlineData("InDiscussion", false)]
    [InlineData("Agreed", false)]
    [InlineData("Declined", true)]
    [InlineData("Completed", true)]
    public void IsFinal_OnlyDeclinedAndCompleted(string status, bool expected)
    {
        Assert.Equal(expected, SponsorshipRequestRules.IsFinal(status));
        Assert.Equal(!expected, SponsorshipRequestRules.BlocksDuplicate(status));
        Assert.Equal(!expected, SponsorshipRequestRules.CanPostMessage(status));
    }

    [Theory]
    [InlineData("Sent", "InDiscussion")]
    [InlineData("Viewed", "InDiscussion")]
    [InlineData("InDiscussion", "InDiscussion")]
    [InlineData("Agreed", "Agreed")]
    public void StatusAfterMessage_MovesSentAndViewedOnly(string status, string expected) =>
        Assert.Equal(expected, SponsorshipRequestRules.StatusAfterMessage(status));

    [Theory]
    [InlineData("Sent", true)]
    [InlineData("Viewed", true)]
    [InlineData("InDiscussion", true)]
    [InlineData("Agreed", false)]
    [InlineData("Declined", false)]
    [InlineData("Completed", false)]
    public void CanDecide_OnlyOpenStatuses(string status, bool expected) =>
        Assert.Equal(expected, SponsorshipRequestRules.CanDecide(status));

    [Theory]
    [InlineData("Agreed", true)]
    [InlineData("Sent", false)]
    [InlineData("Viewed", false)]
    [InlineData("InDiscussion", false)]
    [InlineData("Declined", false)]
    [InlineData("Completed", false)]
    public void CanComplete_OnlyFromAgreed(string status, bool expected) =>
        Assert.Equal(expected, SponsorshipRequestRules.CanComplete(status));

    [Fact]
    public void MarksViewed_OnlyFromSent()
    {
        Assert.True(SponsorshipRequestRules.MarksViewed("Sent"));
        foreach (var status in SponsorshipRequestStatuses.All.Where(s => s != "Sent"))
        {
            Assert.False(SponsorshipRequestRules.MarksViewed(status));
        }
    }

    [Theory]
    [InlineData(Club, "Sent", "message")]
    [InlineData(Club, "Viewed", "message")]
    [InlineData(Club, "InDiscussion", "message")]
    [InlineData(Club, "Agreed", "message,complete")]
    [InlineData(Club, "Declined", "")]
    [InlineData(Club, "Completed", "")]
    [InlineData(Company, "Sent", "message,accept,decline")]
    [InlineData(Company, "Viewed", "message,accept,decline")]
    [InlineData(Company, "InDiscussion", "message,accept,decline")]
    [InlineData(Company, "Agreed", "message,complete")]
    [InlineData(Company, "Declined", "")]
    [InlineData(Company, "Completed", "")]
    public void AllowedActions_PerSideAndStatus(string side, string status, string expected) =>
        Assert.Equal(
            expected.Length == 0 ? [] : expected.Split(','),
            SponsorshipRequestRules.AllowedActions(side, status));
}
