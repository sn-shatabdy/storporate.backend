namespace Storporate.Modules.DiscoveryHiring.Exceptions;

/// <summary>The student has not replied to the invitation yet. Mapped to <c>409 outreach_awaiting_reply</c> by the global exception handler.</summary>
public sealed class OutreachAwaitingReplyException : Exception
{
    public OutreachAwaitingReplyException()
        : base("You can send another message once the student replies.")
    {
    }
}
