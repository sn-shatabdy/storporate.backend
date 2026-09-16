using System.Security.Cryptography;
using System.Text;

namespace Storporate.SharedKernel.Auditing;

/// <summary>
/// Hash-chaining primitives for the <see cref="Entities.AuditLogEntry"/> tamper-evident audit
/// log. Each row's <c>Hash</c> covers every other field on the row plus the previous row's
/// <c>Hash</c>, so any deletion or modification of a past row breaks the chain — the property
/// the rest of STOR-63 (admin query, Integrity Layer) will read against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Canonical form.</b> The eleven fields below are joined by the ASCII Unit Separator
/// control character <c>0x1F</c> (renders invisibly) — NOT a visible character like <c>|</c>.
/// The delimiter is byte-for-byte important: any visible-character substitution here would
/// silently produce a "working" but non-equivalent chain, undetectable from the row data
/// alone. The golden-value unit test pins the exact expected hash for one fixed input tuple
/// so a typo in the delimiter or the field order is caught immediately rather than at chain
/// verification time.
/// </para>
/// <para>
/// <b>Byte-for-byte identical.</b> The algorithm is ported directly from the reference
/// implementation's proven design; the field order, separator, encoding, and hex-case below
/// must not be re-ordered or "cleaned up". A pre-existing
/// <c>tests/.../Auditing/AuditLogHashTests.cs</c> golden-value test pins the exact hash for a
/// known input tuple; any drift fails the test before it can reach production data.
/// </para>
/// </remarks>
public static class AuditLogHash
{
    /// <summary>ASCII Unit Separator control character (0x1F) — the literal byte used as the
    /// delimiter between canonical-form fields. Renders invisibly in most terminals. Do not
    /// substitute a visible character.</summary>
    private const char FieldSeparator = '\u001F';

    /// <summary>
    /// Computes the SHA-256 hash over the canonical concatenation of all eleven fields plus
    /// the prior row's hash. Returns a 64-char lowercase hex string.
    /// </summary>
    /// <param name="previousHash">The prior row's <c>Hash</c> on the same chain, or
    /// <see langword="null"/> for the very first row on a chain. Null is coerced to the empty
    /// string so the canonical form never contains a literal <see langword="null"/> byte.</param>
    /// <param name="id">The new row's <see cref="Guid"/> id, formatted as <c>"D"</c>
    /// (lowercase, hyphenated, 36 chars).</param>
    /// <param name="accountId">The new row's account id, or <see langword="null"/> for rows
    /// in the shared pre-account chain. Formatted as <c>"D"</c> when present, empty string
    /// otherwise.</param>
    /// <param name="actorUserId">The acting user's id, or <see langword="null"/>. Same
    /// formatting as <paramref name="accountId"/>.</param>
    /// <param name="action">The action verb (e.g. <c>"login_succeeded"</c>). Required.</param>
    /// <param name="resourceType">The resource kind (e.g. <c>"User"</c>). Required.</param>
    /// <param name="resourceId">The targeted resource's identifier, or <see langword="null"/>.
    /// Empty string when null.</param>
    /// <param name="ipAddress">Raw TCP peer IP, or <see langword="null"/>. Empty string when
    /// null.</param>
    /// <param name="userAgent">User-Agent header value (already truncated to 512 chars by the
    /// middleware), or <see langword="null"/>. Empty string when null.</param>
    /// <param name="metadataJson">JSON metadata, or <see langword="null"/>. Empty string when
    /// null.</param>
    /// <param name="createdAtUtc">UTC timestamp, formatted via <c>"O"</c> (round-trip, 27 chars,
    /// always suffixed <c>Z</c>). <see cref="DateTime.Kind"/> is normalized to
    /// <see cref="DateTimeKind.Utc"/> first so a <see cref="DateTimeKind.Unspecified"/> or
    /// <see cref="DateTimeKind.Local"/> value produces the same hash as its UTC twin.</param>
    /// <returns>64-character lowercase hex SHA-256 digest.</returns>
    public static string Compute(
        string? previousHash,
        Guid id,
        Guid? accountId,
        Guid? actorUserId,
        string action,
        string resourceType,
        string? resourceId,
        string? ipAddress,
        string? userAgent,
        string? metadataJson,
        DateTime createdAtUtc)
    {
        // Field order MUST match the plan's documented 1–11 sequence. Any reorder silently
        // changes every hash and breaks the chain. The constants aren't factored out because
        // the canonical order is itself part of the contract — inlining makes the order
        // visually obvious to a future maintainer who has to add a field.
        var canonical = new StringBuilder(capacity: 256)
            .Append(previousHash ?? string.Empty)
            .Append(FieldSeparator)
            .Append(id.ToString("D"))
            .Append(FieldSeparator)
            .Append(accountId?.ToString("D") ?? string.Empty)
            .Append(FieldSeparator)
            .Append(actorUserId?.ToString("D") ?? string.Empty)
            .Append(FieldSeparator)
            .Append(action)
            .Append(FieldSeparator)
            .Append(resourceType)
            .Append(FieldSeparator)
            .Append(resourceId ?? string.Empty)
            .Append(FieldSeparator)
            .Append(ipAddress ?? string.Empty)
            .Append(FieldSeparator)
            .Append(userAgent ?? string.Empty)
            .Append(FieldSeparator)
            .Append(metadataJson ?? string.Empty)
            .Append(FieldSeparator)
            // "O" round-trip format always emits Kind=Utc as a trailing "Z"; forcing Kind
            // first means a caller that forgot to call .ToUniversalTime() still produces a
            // hash that's consistent with the stored column value (which is timestamptz).
            .Append(DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc).ToString("O"))
            .ToString();

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));

        // Lowercase hex (no separators) — 64 chars exactly. Convert.ToHexStringLower is the
        // .NET 9+ lowercase variant of Convert.ToHexString; pinning the lowercase form so
        // the on-disk representation and any future integrity verifier agree on casing.
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Truncates a <see cref="DateTime"/> to microsecond precision by zeroing the sub-
    /// microsecond ticks. PostgreSQL <c>timestamp with time zone</c> stores microseconds, so
    /// any finer-grained value (from <see cref="DateTime.UtcNow"/>'s 100-ns ticks) would round
    /// to a different value on round-trip and break the hash. Preserves
    /// <see cref="DateTime.Kind"/>.
    /// </summary>
    /// <param name="value">Any <see cref="DateTime"/>; its <see cref="DateTime.Kind"/> is
    /// preserved.</param>
    /// <returns>A new <see cref="DateTime"/> with the same <see cref="DateTime.Kind"/> and
    /// ticks rounded down to a 10-tick (microsecond) boundary.</returns>
    public static DateTime TruncateToMicroseconds(DateTime value) =>
        new(value.Ticks - (value.Ticks % 10), value.Kind);
}
