namespace Storporate.SharedKernel.Auditing;

/// <summary>
/// Canonical <c>MetadataJson</c> payloads for the small set of audit-event reasons that
/// recur across handlers. Keeps every "actor type missing", "wrong code", etc. row queryable
/// by the same exact string from a future admin UI filter, and lets the handlers drop the
/// inline JSON literals that would otherwise drift across call sites.
/// </summary>
public static class AuditReasons
{
    /// <summary>New account registration was attempted without an <c>actorType</c>.</summary>
    public const string ActorTypeMissing = """{"reason":"actor_type_missing"}""";

    /// <summary>New account registration supplied an <c>actorType</c> outside the
    /// per-endpoint allowlist (e.g. <see cref="Security.ActorTypes.Administrator"/>).</summary>
    public const string ActorTypeUnrecognized = """{"reason":"actor_type_unrecognized"}""";

    /// <summary><c>/api/auth/otp/verify</c> was called with no matching pending code.</summary>
    public const string OtpNoPendingCode = """{"reason":"no_pending_code"}""";

    /// <summary>The presented OTP code's expiry timestamp had already passed.</summary>
    public const string OtpExpired = """{"reason":"expired"}""";

    /// <summary>The presented OTP code did not match the hashed value on file.</summary>
    public const string OtpWrongCode = """{"reason":"wrong_code"}""";

    /// <summary><c>/api/auth/otp/verify</c> in a Development environment accepted the configured
    /// <c>Otp:MasterCode</c> and bypassed the normal lookup/expiry/attempt checks. The bypass still
    /// also writes the normal <c>login_succeeded</c> row keyed to the resulting user — this row
    /// is the marker that lets a future audit-log query distinguish a dev-bypass login from a
    /// real-code login without re-walking OtpCode rows.</summary>
    public const string OtpDevMasterCodeUsed = """{"reason":"dev_master_code"}""";
}
