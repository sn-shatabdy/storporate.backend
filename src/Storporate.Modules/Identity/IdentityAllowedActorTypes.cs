using Storporate.SharedKernel.Entities;

namespace Storporate.Modules.Identity;

/// <summary>
/// Actor types a brand-new user is allowed to register as through a self-service auth flow
/// (OTP verify, Google login). Deliberately excludes <see cref="ActorTypes.Administrator"/> —
/// Administrators are platform-staff accounts that bypass workspace isolation (see STOR-62) and
/// must be provisioned out-of-band, never self-registered through a public auth flow.
/// </summary>
public static class IdentityAllowedActorTypes
{
    public static readonly IReadOnlySet<string> Set = new HashSet<string>(StringComparer.Ordinal)
    {
        ActorTypes.Student,
        ActorTypes.Organization,
        ActorTypes.University,
        ActorTypes.Club,
    };
}