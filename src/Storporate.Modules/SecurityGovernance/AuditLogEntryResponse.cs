namespace Storporate.Modules.SecurityGovernance;

/// <summary>
/// Response DTO for a single <see cref="Storporate.SharedKernel.Entities.AuditLogEntry"/> as
/// surfaced through <c>GET /api/security-governance/audit-log</c>. Mirrors the entity
/// one-for-one <em>except</em> for <c>Hash</c> and <c>PreviousHash</c>, which stay in the
/// database for STOR-45's Integrity Layer to verify against but are not meaningful to a
/// human viewer reading the audit feed.
/// </summary>
public sealed record AuditLogEntryResponse(
    Guid Id,
    long SequenceNumber,
    Guid? AccountId,
    Guid? ActorUserId,
    string Action,
    string ResourceType,
    string? ResourceId,
    string? IpAddress,
    string? UserAgent,
    string? MetadataJson,
    DateTime CreatedAt);