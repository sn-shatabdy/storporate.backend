using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.InstitutionalClubNetwork.SponsorshipRequests;

/// <summary>
/// The state rules of a sponsorship request (STOR-72), pure and unit tested. Sent, Viewed and
/// InDiscussion are open; the company may accept or decline from any of them. Agreed can only end
/// in Completed. Declined and Completed are final.
/// </summary>
public static class SponsorshipRequestRules
{
    public const string ActionMessage = "message";
    public const string ActionAccept = "accept";
    public const string ActionDecline = "decline";
    public const string ActionComplete = "complete";

    public const int MaxMessages = 200;
    public const int MaxMessageLength = 2000;

    public static bool IsFinal(string status) =>
        status is SponsorshipRequestStatuses.Declined or SponsorshipRequestStatuses.Completed;

    /// <summary>A duplicate is a request for the same club, goal set and event title that is not final.</summary>
    public static bool BlocksDuplicate(string status) => !IsFinal(status);

    public static bool CanPostMessage(string status) => !IsFinal(status);

    /// <summary>Sent and Viewed move to InDiscussion on a message; every other status is kept.</summary>
    public static string StatusAfterMessage(string status) =>
        status is SponsorshipRequestStatuses.Sent or SponsorshipRequestStatuses.Viewed
            ? SponsorshipRequestStatuses.InDiscussion
            : status;

    public static bool CanDecide(string status) =>
        status is SponsorshipRequestStatuses.Sent
            or SponsorshipRequestStatuses.Viewed
            or SponsorshipRequestStatuses.InDiscussion;

    public static bool CanComplete(string status) => status == SponsorshipRequestStatuses.Agreed;

    /// <summary>Only the first company open of a Sent request changes anything.</summary>
    public static bool MarksViewed(string status) => status == SponsorshipRequestStatuses.Sent;

    /// <summary>The actions the given side may take now, in a stable order.</summary>
    public static IReadOnlyList<string> AllowedActions(string side, string status)
    {
        var actions = new List<string>(4);
        if (CanPostMessage(status))
        {
            actions.Add(ActionMessage);
        }

        if (side == SponsorshipRequestSides.Company && CanDecide(status))
        {
            actions.Add(ActionAccept);
            actions.Add(ActionDecline);
        }

        if (CanComplete(status))
        {
            actions.Add(ActionComplete);
        }

        return actions;
    }
}
