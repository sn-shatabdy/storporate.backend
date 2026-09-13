namespace Storporate.SharedKernel.Entities;

/// <summary>
/// The allowed values for <see cref="User.ActorType"/>. Kept as string constants (not an enum),
/// mirroring <see cref="JobStatus"/>, so the database column stays a readable string and adding a
/// new actor type later doesn't require a migration to widen a numeric range. Chosen once at
/// registration (STOR-61); role *enforcement* (what each type can actually do) is STOR-62's job,
/// not this one. Confirmer (a 5th eventual actor type, per STOR-42) is explicitly out of scope.
/// </summary>
public static class ActorTypes
{
    public const string Student = "Student";
    public const string Employer = "Employer";
    public const string UniversityAdmin = "UniversityAdmin";
    public const string ClubAdmin = "ClubAdmin";
}
