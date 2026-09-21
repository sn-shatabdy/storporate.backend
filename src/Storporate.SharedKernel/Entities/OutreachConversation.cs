namespace Storporate.SharedKernel.Entities;

/// <summary>
/// One employer-to-student conversation (STOR-68); at most one per (organization, student).
/// Non-tenant: handlers scope by <see cref="OrganizationAccountId"/> (employer side) or
/// <see cref="StudentAccountId"/> (student side). Neither party's contact details are stored here:
/// each side only ever sees the other's display name snapshot.
/// </summary>
public sealed class OutreachConversation
{
    public Guid Id { get; set; }

    public Guid OrganizationAccountId { get; set; }

    public User? Organization { get; set; }

    public Guid StudentAccountId { get; set; }

    public User? Student { get; set; }

    /// <summary>Organization name the employer typed when inviting; the only employer identity the student sees.</summary>
    public required string OrganizationName { get; set; }

    /// <summary>Student display name copied from the talent index entry at invite time.</summary>
    public required string StudentDisplayName { get; set; }

    public required string Status { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public List<OutreachMessage> Messages { get; set; } = [];
}

public static class OutreachStatuses
{
    public const string Invited = "Invited";
    public const string Replied = "Replied";
    public const string Declined = "Declined";
}

public static class OutreachSenderRoles
{
    public const string Organization = "Organization";
    public const string Student = "Student";
}
