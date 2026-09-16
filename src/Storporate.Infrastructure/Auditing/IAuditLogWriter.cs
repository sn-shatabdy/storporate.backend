namespace Storporate.Infrastructure.Auditing;

/// <summary>
/// Writes a single tamper-evident audit row to the <c>AuditLogEntries</c> table. Each row's
/// hash covers the prior row's hash on the same chain so that deleting or modifying any past
/// row is detectable by recomputing and comparing hashes.
/// </summary>
/// <remarks>
/// <para>
/// The writer pulls everything it needs from the ambient <c>IAccountContext</c> — the
/// authenticated caller, the account the call is being made in, the remote IP, the
/// User-Agent header — so callers don't have to pass them in. The only inputs are what the
/// event itself is about: the action verb, the targeted resource kind, an optional resource
/// id, and an optional JSON metadata blob for event-specific data.
/// </para>
/// <para>
/// Phase 2 of STOR-63 wires this into the OTP/session/permission endpoints; Phase 3 adds
/// the Administrator query endpoint that reads the rows back.
/// </para>
/// </remarks>
public interface IAuditLogWriter
{
    /// <summary>
    /// Appends one row to the audit log. The new row's <c>Hash</c> covers the prior row's
    /// <c>Hash</c> on the same chain (same <c>AccountId</c>, or the shared
    /// <see langword="null"/>-<c>AccountId</c> chain for pre-account events).
    /// </summary>
    /// <param name="action">Short, stable verb identifying what happened (e.g.
    /// <c>"login_succeeded"</c>, <c>"otp_requested"</c>, <c>"permission_denied"</c>). The
    /// catalog is documented in the STOR-63 plan and pinned by Phase 2's handler tests.</param>
    /// <param name="resourceType">The kind of resource the action targeted (e.g.
    /// <c>"User"</c>, <c>"Session"</c>, <c>"Permission"</c>).</param>
    /// <param name="resourceId">The targeted resource's identifier (a <see cref="Guid"/>, an
    /// email, a permission string — whatever's natural for the resource). Null when the
    /// event has no specific resource.</param>
    /// <param name="metadataJson">Optional JSON metadata describing the event in more detail.
    /// Stored as <c>jsonb</c>. Pass <see langword="null"/> to skip the column.</param>
    /// <param name="cancellationToken">Forwarded to the underlying DB call so a client
    /// disconnect cancels the write cleanly.</param>
    Task WriteAsync(
        string action,
        string resourceType,
        string? resourceId,
        string? metadataJson = null,
        CancellationToken cancellationToken = default);
}
