namespace Storporate.Modules.DiscoveryHiring.Exceptions;

/// <summary>The student declined the conversation, so no further invitations or messages can be sent. Mapped to <c>409 outreach_declined</c> by the global exception handler.</summary>
public sealed class OutreachDeclinedException : Exception
{
    public OutreachDeclinedException()
        : base("This student declined the conversation.")
    {
    }
}
