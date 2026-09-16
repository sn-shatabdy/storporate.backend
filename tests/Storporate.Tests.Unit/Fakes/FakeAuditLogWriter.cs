using Storporate.Infrastructure.Auditing;

namespace Storporate.Tests.Unit.Fakes;

/// <summary>
/// Test double for <see cref="IAuditLogWriter"/>. Records every call into an in-memory list
/// so handler tests can assert which <c>Action</c> / <c>ResourceType</c> / <c>ResourceId</c>
/// / <c>MetadataJson</c> tuple was written for a given event, without going through the real
/// Postgres-backed writer (which no-ops under the EF InMemory provider the unit suite uses —
/// see <see cref="AuditLogWriter"/>'s provider gate).
/// </summary>
/// <remarks>
/// Hand-written (no mocking framework) to match the rest of this suite's
/// (<c>FakeGoogleIdTokenValidator</c>, <c>FakeJwtTokenService</c>, <c>RecordingJwtTokenService</c>)
/// style: small, deterministic, with a public read-only view of the recorded calls.
/// </remarks>
public sealed class FakeAuditLogWriter : IAuditLogWriter
{
    private readonly List<RecordedAuditEntry> _recorded = [];

    /// <summary>Every <see cref="WriteAsync"/> call, in invocation order.</summary>
    public IReadOnlyList<RecordedAuditEntry> Recorded => _recorded;

    public Task WriteAsync(
        string action,
        string resourceType,
        string? resourceId,
        string? metadataJson = null,
        CancellationToken cancellationToken = default)
    {
        _recorded.Add(new RecordedAuditEntry(action, resourceType, resourceId, metadataJson));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Single recorded call to <see cref="IAuditLogWriter.WriteAsync"/>. Held by value so each
/// row is immutable once captured; the fake's <c>Recorded</c> list is read-only.
/// </summary>
public sealed record RecordedAuditEntry(
    string Action,
    string ResourceType,
    string? ResourceId,
    string? MetadataJson);
