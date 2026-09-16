using Storporate.SharedKernel.Auditing;

namespace Storporate.Tests.Unit.Auditing;

/// <summary>
/// Pins the <see cref="AuditLogHash"/> canonicalization contract. The golden-value test
/// exists to catch a silent regression in field order, separator byte, encoding, or hex case
/// — any of which would produce a "working" but non-equivalent chain that only surfaces at
/// integrity-verification time, far too late.
/// </summary>
public class AuditLogHashTests
{
    [Fact]
    public void Compute_GoldenValue_MatchesExpectedHash()
    {
        // Fixed input tuple, deliberately chosen so the canonical form is deterministic
        // and the hash below cannot drift. Any change to field order, separator, encoding,
        // or hex case breaks this assertion immediately. The expected hash below is
        // hand-computed by running AuditLogHash.Compute on this exact input tuple.
        // SHA-256( canonical-form-bytes-of-the-input-below ) =
        // 50d2dfb4daa1d4a3469e0aebc24665505379be1c881ef05194b760f5f26a9416
        const string expectedHash = "50d2dfb4daa1d4a3469e0aebc24665505379be1c881ef05194b760f5f26a9416";

        var actual = AuditLogHash.Compute(
            previousHash: null,
            id: new Guid("00000000-0000-0000-0000-000000000001"),
            accountId: new Guid("00000000-0000-0000-0000-000000000002"),
            actorUserId: new Guid("00000000-0000-0000-0000-000000000003"),
            action: "login_succeeded",
            resourceType: "User",
            resourceId: "00000000-0000-0000-0000-000000000003",
            ipAddress: "127.0.0.1",
            userAgent: "curl/8.0",
            metadataJson: "{\"foo\":\"bar\"}",
            createdAtUtc: new DateTime(2026, 9, 16, 12, 34, 56, 789, DateTimeKind.Utc));

        Assert.Equal(expectedHash, actual);
        Assert.Equal(64, actual.Length);
        Assert.Equal(actual.ToLowerInvariant(), actual);
    }

    [Fact]
    public void Compute_SameInputs_ReturnsSameHash()
    {
        var first = AuditLogHash.Compute(
            previousHash: "abc",
            id: new Guid("00000000-0000-0000-0000-000000000001"),
            accountId: new Guid("00000000-0000-0000-0000-000000000002"),
            actorUserId: new Guid("00000000-0000-0000-0000-000000000003"),
            action: "login_succeeded",
            resourceType: "User",
            resourceId: "u-1",
            ipAddress: "127.0.0.1",
            userAgent: "curl/8.0",
            metadataJson: null,
            createdAtUtc: new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc));

        var second = AuditLogHash.Compute(
            previousHash: "abc",
            id: new Guid("00000000-0000-0000-0000-000000000001"),
            accountId: new Guid("00000000-0000-0000-0000-000000000002"),
            actorUserId: new Guid("00000000-0000-0000-0000-000000000003"),
            action: "login_succeeded",
            resourceType: "User",
            resourceId: "u-1",
            ipAddress: "127.0.0.1",
            userAgent: "curl/8.0",
            metadataJson: null,
            createdAtUtc: new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc));

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("login_succeeded", "login_failed")]
    [InlineData("User", "Session")]
    [InlineData("u-1", "u-2")]
    [InlineData("127.0.0.1", "127.0.0.2")]
    [InlineData("curl/8.0", "curl/8.1")]
    [InlineData("{\"a\":1}", "{\"a\":2}")]
    public void Compute_ChangingAnyField_ChangesHash(string firstValue, string secondValue)
    {
        // Build two hashes that differ in exactly one field; the field under test is
        // intentionally the last positional argument so each Theory row can pin it
        // without a custom lambda. The other fields are kept identical.
        var createdAt = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

        // The Theory rows above vary `action`, `resourceType`, etc. — we map the row to
        // a single field by rotating the argument position. Easier: build a full pair of
        // hashes for each row by hand, varying one field at a time. Inline below.
        var hashForFirst = AuditLogHash.Compute(
            previousHash: "p",
            id: new Guid("00000000-0000-0000-0000-000000000001"),
            accountId: new Guid("00000000-0000-0000-0000-000000000002"),
            actorUserId: new Guid("00000000-0000-0000-0000-000000000003"),
            action: firstValue == "login_succeeded" || firstValue == "login_failed" ? firstValue : "login_succeeded",
            resourceType: firstValue == "User" || firstValue == "Session" ? firstValue : "User",
            resourceId: firstValue == "u-1" || firstValue == "u-2" ? firstValue : "u-1",
            ipAddress: firstValue == "127.0.0.1" || firstValue == "127.0.0.2" ? firstValue : "127.0.0.1",
            userAgent: firstValue == "curl/8.0" || firstValue == "curl/8.1" ? firstValue : "curl/8.0",
            metadataJson: firstValue == "{\"a\":1}" || firstValue == "{\"a\":2}" ? firstValue : null,
            createdAtUtc: createdAt);

        var hashForSecond = AuditLogHash.Compute(
            previousHash: "p",
            id: new Guid("00000000-0000-0000-0000-000000000001"),
            accountId: new Guid("00000000-0000-0000-0000-000000000002"),
            actorUserId: new Guid("00000000-0000-0000-0000-000000000003"),
            action: secondValue == "login_succeeded" || secondValue == "login_failed" ? secondValue : "login_succeeded",
            resourceType: secondValue == "User" || secondValue == "Session" ? secondValue : "User",
            resourceId: secondValue == "u-1" || secondValue == "u-2" ? secondValue : "u-1",
            ipAddress: secondValue == "127.0.0.1" || secondValue == "127.0.0.2" ? secondValue : "127.0.0.1",
            userAgent: secondValue == "curl/8.0" || secondValue == "curl/8.1" ? secondValue : "curl/8.0",
            metadataJson: secondValue == "{\"a\":1}" || secondValue == "{\"a\":2}" ? secondValue : null,
            createdAtUtc: createdAt);

        Assert.NotEqual(hashForFirst, hashForSecond);
    }

    [Fact]
    public void Compute_ChangingPreviousHash_ChangesHash()
    {
        var hashForNullPrev = AuditLogHash.Compute(
            previousHash: null,
            id: new Guid("00000000-0000-0000-0000-000000000001"),
            accountId: new Guid("00000000-0000-0000-0000-000000000002"),
            actorUserId: null,
            action: "a",
            resourceType: "b",
            resourceId: null,
            ipAddress: null,
            userAgent: null,
            metadataJson: null,
            createdAtUtc: new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc));

        var hashForNonNullPrev = AuditLogHash.Compute(
            previousHash: "abc",
            id: new Guid("00000000-0000-0000-0000-000000000001"),
            accountId: new Guid("00000000-0000-0000-0000-000000000002"),
            actorUserId: null,
            action: "a",
            resourceType: "b",
            resourceId: null,
            ipAddress: null,
            userAgent: null,
            metadataJson: null,
            createdAtUtc: new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc));

        Assert.NotEqual(hashForNullPrev, hashForNonNullPrev);
    }

    [Fact]
    public void Compute_LocalAndUnspecifiedCreatedAt_HashAsUtc()
    {
        // SpecifyKind forces Kind=Utc inside Compute; both inputs below must produce the
        // same hash. This protects callers that forget .ToUniversalTime() at the call site.
        var localInput = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Local);
        var unspecifiedInput = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Unspecified);
        var utcInput = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

        var hashLocal = AuditLogHash.Compute(
            previousHash: null, id: Guid.Empty, accountId: null, actorUserId: null,
            action: "a", resourceType: "b", resourceId: null,
            ipAddress: null, userAgent: null, metadataJson: null,
            createdAtUtc: localInput);

        var hashUnspecified = AuditLogHash.Compute(
            previousHash: null, id: Guid.Empty, accountId: null, actorUserId: null,
            action: "a", resourceType: "b", resourceId: null,
            ipAddress: null, userAgent: null, metadataJson: null,
            createdAtUtc: unspecifiedInput);

        var hashUtc = AuditLogHash.Compute(
            previousHash: null, id: Guid.Empty, accountId: null, actorUserId: null,
            action: "a", resourceType: "b", resourceId: null,
            ipAddress: null, userAgent: null, metadataJson: null,
            createdAtUtc: utcInput);

        Assert.Equal(hashUtc, hashLocal);
        Assert.Equal(hashUtc, hashUnspecified);
    }

    [Fact]
    public void TruncateToMicroseconds_ZeroesSubMicrosecondTicks()
    {
        // DateTime ticks are 100ns; one tick past a microsecond boundary (10 ticks) must be
        // truncated down to the boundary.
        var input = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc).AddTicks(15);
        var truncated = AuditLogHash.TruncateToMicroseconds(input);

        Assert.Equal(input.Ticks - 5, truncated.Ticks);
        Assert.Equal(DateTimeKind.Utc, truncated.Kind);
    }

    [Fact]
    public void TruncateToMicroseconds_PreservesKind()
    {
        var local = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Local);
        var unspecified = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Unspecified);

        Assert.Equal(DateTimeKind.Local, AuditLogHash.TruncateToMicroseconds(local).Kind);
        Assert.Equal(DateTimeKind.Unspecified, AuditLogHash.TruncateToMicroseconds(unspecified).Kind);
    }
}
