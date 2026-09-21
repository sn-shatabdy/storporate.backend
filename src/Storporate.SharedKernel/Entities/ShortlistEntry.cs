namespace Storporate.SharedKernel.Entities;

/// <summary>
/// An employer's saved candidate (STOR-68). Non-tenant: it is scoped explicitly by
/// <see cref="OrganizationAccountId"/> in the shortlist handlers, never through the tenant filter.
/// <see cref="StudentAccountId"/> is resolved server-side from the talent index entry and is never
/// returned to the employer.
/// </summary>
public sealed class ShortlistEntry
{
    public Guid Id { get; set; }

    public Guid OrganizationAccountId { get; set; }

    public User? Organization { get; set; }

    public Guid StudentAccountId { get; set; }

    public User? Student { get; set; }

    /// <summary>The <c>TalentIndexEntry.Id</c> that identified the student when they were saved.</summary>
    public Guid CandidateId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
