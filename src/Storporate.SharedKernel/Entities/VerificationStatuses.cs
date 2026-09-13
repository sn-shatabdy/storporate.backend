namespace Storporate.SharedKernel.Entities;

/// <summary>
/// The allowed values for <see cref="User.VerificationStatus"/>. Kept as string constants (not an
/// enum), mirroring <see cref="JobStatus"/>. Real institutional identity verification of
/// non-student accounts (confirming an employer/university/club really is who it claims) is
/// STOR-50's job — this story only tags the initial status for that later story to act on.
/// </summary>
public static class VerificationStatuses
{
    public const string Verified = "Verified";
    public const string Unverified = "Unverified";
}
