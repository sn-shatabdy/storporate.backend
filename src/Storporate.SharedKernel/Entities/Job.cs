namespace Storporate.SharedKernel.Entities;

/// <summary>
/// A durable, Postgres-backed background job record. This is schema only for STOR-59 — no
/// processing/worker logic is implemented yet (out of scope for this story).
/// </summary>
public sealed class Job
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

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }
}
