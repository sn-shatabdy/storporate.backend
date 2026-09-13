namespace Storporate.SharedKernel.Entities;

/// <summary>
/// The allowed values for <see cref="Job.Status"/>. Kept as string constants (not an enum) so the
/// database column stays a readable string and new statuses don't require a migration to widen a
/// numeric range.
/// </summary>
public static class JobStatus
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}
