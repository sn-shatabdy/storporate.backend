namespace Storporate.Modules.DiscoveryHiring.Exceptions;

/// <summary>An invitation to this student has already been sent. Mapped to <c>409 outreach_already_started</c> by the global exception handler.</summary>
public sealed class OutreachAlreadyStartedException : Exception
{
    public OutreachAlreadyStartedException()
        : base("You have already contacted this student.")
    {
    }
}
