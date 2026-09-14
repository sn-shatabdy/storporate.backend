namespace Storporate.SharedKernel.Entities;

/// <summary>
/// The allowed values for <see cref="User.ActorType"/>. Kept as string constants (not an enum),
/// mirroring <see cref="JobStatus"/>, so the database column stays a readable string and adding a
/// new actor type later doesn't require a migration to widen a numeric range. Chosen once at
/// registration (STOR-61); role *enforcement* (what each type can actually do) is STOR-62's job,
/// not this one. Confirmer (an eventual actor type, per STOR-42) is explicitly out of scope.
/// </summary>
/// <remarks>
/// <see cref="Administrator"/> is a platform-staff role rather than a self-service account type.
/// By design it bypasses the per-account workspace isolation that the other four actor types are
/// subject to (see STOR-62 plan, Context &amp; Findings — Administrator's isolation model
/// differs from the other four), so it can read and write across every account for support and
/// moderation. The bypass is enforced via the SecurityGovernance authorization module wired in
/// Phase 3 (custom <c>PermissionAuthorizationHandler</c> short-circuit + matching PostgreSQL row-
/// level security policy in Phase 5), not by application-code holes.
/// </remarks>
public static class ActorTypes
{
    public const string Student = "Student";
    public const string Organization = "Organization";
    public const string University = "University";
    public const string Club = "Club";

    /// <summary>Platform staff account that bypasses workspace isolation by design. See class
    /// remarks for the authorization/RLS enforcement story.</summary>
    public const string Administrator = "Administrator";
}