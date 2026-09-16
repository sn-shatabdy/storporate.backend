namespace Storporate.SharedKernel.Entities;


/// <summary>
/// A tamper-evident audit trail row. Every row's <see cref="Hash"/> covers the previous row's
/// <see cref="Hash"/> for the same <see cref="AccountId"/> (plus a shared chain for all
/// <see langword="null"/>-<see cref="AccountId"/> rows), so deleting or altering any historical
/// row breaks the chain and is detectable by recomputing and comparing hashes — the trust
/// substrate that <c>STOR-45</c> (The Integrity Layer) and <c>STOR-42</c> (The Confirmer Network)
/// will read against.
/// </summary>
/// <remarks>
/// <para>
/// Schema-only POCO; mapping is done in <c>AuditLogEntryConfiguration</c>. Intentionally does
/// <em>not</em> implement <see cref="IAccountScoped"/>: the interface requires a non-nullable
/// <see cref="AccountId"/>, but audit rows legitimately have a <see langword="null"/>
/// <see cref="AccountId"/> (e.g. a failed OTP request before any account exists), and access
/// control for this table is enforced entirely by the Administrator-only query endpoint's
/// permission check, not by a tenant query filter — keeping the table readable to the audit
/// view without having to opt-in to STOR-62's three-layer workspace-isolation pipeline.
/// </para>
/// <para>
/// Written via raw SQL (<see cref="Infrastructure.Auditing.AuditLogWriter"/>) rather than EF
/// Core's change tracker so the hash-chain insert can run inside its own transaction with its
/// own advisory lock, independent of whatever transaction / DbContext the calling request
/// might be using. The entity itself is still a normal EF Core-mapped type so the
/// Administrator query endpoint can read rows back through <c>WriteDbContext.AuditLogEntries</c>
/// the same way every other module reads its data.
/// </para>
/// </remarks>
public sealed class AuditLogEntry
{
    /// <summary>Maximum bytes captured from the User-Agent header. Mirrored on the column
    /// definition (<c>AuditLogEntryConfiguration.HasMaxLength(UserAgentMaxLength)</c>) and
    /// on the middleware that truncates the captured value (<c>AccountContextMiddleware</c>);
    /// defining it once on the entity keeps all three sites from silently drifting.</summary>
    public const int UserAgentMaxLength = 512;

    public Guid Id { get; set; }

    /// <summary>Postgres identity-always sequence, allocated server-side per insert. Indexed
    /// uniquely and paired with <see cref="AccountId"/> for fast chain-tip lookup per account.
    /// </summary>
    public long SequenceNumber { get; set; }

    /// <summary>The account (workspace) this row belongs to. <see langword="null"/> for
    /// pre-account events (e.g. an OTP request before the user exists). All
    /// <see langword="null"/>-<see cref="AccountId"/> rows share one chain, keyed by lock
    /// value <c>0</c>.</summary>
    public Guid? AccountId { get; set; }

    /// <summary>The acting user's <see cref="User.Id"/>, when the call is authenticated. Null
    /// for pre-authentication events (same scope as <see cref="AccountId"/> being null).</summary>
    public Guid? ActorUserId { get; set; }

    /// <summary>Short, stable verb identifying what happened (e.g. <c>"login_succeeded"</c>,
    /// <c>"permission_denied"</c>, <c>"otp_requested"</c>). See STOR-63 Phase 2 for the full
    /// catalog. Required, maxlength 200.</summary>
    public required string Action { get; set; }

    /// <summary>The kind of resource the action targeted (e.g. <c>"User"</c>, <c>"Session"</c>,
    /// <c>"Permission"</c>). Required, maxlength 100.</summary>
    public required string ResourceType { get; set; }

    /// <summary>The targeted resource's identifier (a <see cref="Guid"/>, an email, a
    /// permission string — whatever's natural for the resource). Null when the event has no
    /// specific resource (e.g. a rate-limit rejection).</summary>
    public string? ResourceId { get; set; }

    /// <summary>Raw TCP peer IP captured by <c>AccountContextMiddleware</c> from
    /// <c>HttpContext.Connection.RemoteIpAddress</c>. Null when the caller is on the
    /// pre-authentication path and middleware hasn't populated it yet, or when the connection
    /// has no remote address (loopback tests). Maxlength 64.</summary>
    public string? IpAddress { get; set; }

    /// <summary>User-Agent header value captured by <c>AccountContextMiddleware</c>, truncated
    /// to 512 characters to match the DB column width.</summary>
    public string? UserAgent { get; set; }

    /// <summary>JSON metadata describing the event in more detail (jsonb). Stored as a string
    /// here; cast to <c>::jsonb</c> at INSERT time by the raw-SQL writer.</summary>
    public string? MetadataJson { get; set; }

    /// <summary>UTC timestamp the row was created. Truncated to microsecond precision via
    /// <c>AuditLogHash.TruncateToMicroseconds</c> before hashing so the stored value and the
    /// hashed value are byte-equal on every read.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>SHA-256 hex of the canonical concatenation of all 11 hash fields plus the
    /// previous row's <see cref="Hash"/>. Always 64 lowercase hex chars. The chain-integrity
    /// property: tampering with any field or deleting any row breaks subsequent hashes.</summary>
    public required string Hash { get; set; }

    /// <summary>The previous row's <see cref="Hash"/> on the same chain (same
    /// <see cref="AccountId"/>, or the shared <see langword="null"/>-<see cref="AccountId"/>
    /// chain). Null only for the very first row on a chain.</summary>
    public string? PreviousHash { get; set; }
}
