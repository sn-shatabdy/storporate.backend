namespace Storporate.SharedKernel.Entities;

/// <summary>
/// A durable, Postgres-backed background job record. This is schema only for STOR-59 — no
/// processing/worker logic is implemented yet (out of scope for this story).
/// </summary>
/// <remarks>
/// STOR-62 Phase 1: <see cref="Job"/> is the first <see cref="IAccountScoped"/> entity — it
/// is what proves out the three-layer isolation pipeline (global query filter, save-time
/// interceptor, PostgreSQL row-level security policy) end-to-end before any future story adds
/// more account-owned entities. <see cref="AccountId"/> foreign-keys to <see cref="User.Id"/>
/// (a job's owner-account is itself a <see cref="User"/> row — same reasoning as
/// <see cref="IAccountScoped"/>'s class remarks: workspace = account, no join table).
/// </remarks>
public sealed class Job : IAccountScoped
{
    public Guid Id { get; set; }

    /// <summary>Discriminator identifying which job handler should process this record.</summary>
    public required string Type { get; set; }

    /// <summary>The job's input payload, stored as JSON (jsonb).</summary>
    public required string PayloadJson { get; set; }

    /// <summary>One of <see cref="JobStatus"/>.</summary>
    public string Status { get; set; } = JobStatus.Pending;

    public int AttemptCount { get; set; }

    /// <summary>Populated when <see cref="Status"/> is <see cref="JobStatus.Failed"/>.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>The owning <see cref="User.Id"/>. See class remarks.</summary>
    public Guid AccountId { get; set; }

    /// <summary>Navigation to the owning <see cref="User"/>. Not required at insert time —
    /// EF Core populates it from <see cref="AccountId"/> when the principal is loaded.</summary>
    public User? Account { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}