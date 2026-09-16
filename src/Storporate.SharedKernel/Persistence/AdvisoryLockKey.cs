namespace Storporate.SharedKernel.Persistence;

/// <summary>
/// Computes a 64-bit <c>pg_advisory_xact_lock</c> key from a <see cref="Guid"/>. Used by
/// <c>AuditLogWriter</c> to serialize concurrent audit-row inserts on the same chain so the
/// chain tip read sees a consistent previous hash and the resulting chain is gap-free.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Guid"/> is 128 bits; Postgres advisory locks take a <c>bigint</c>, so the two
/// 64-bit halves of the GUID are XOR'd together. Collisions are possible but vanishingly
/// unlikely for the chain-per-account cardinality (low millions of distinct GUIDs produce a
/// collision probability on the order of 10^-8 — see "birthday problem" with 2^64 buckets)
/// and a same-lock collision between two <em>different</em> accounts would only delay one
/// insert behind the other, never corrupt data. Good enough for the basic / prototype-scale
/// "basic" audit story; a later Integrity Layer story can revisit if contention ever
/// materializes.
/// </para>
/// <para>
/// <see langword="null"/> inputs map to lock key <c>0</c>, so all rows with a
/// <see langword="null"/> account (pre-account events) share a single chain — by design, per
/// the plan's "null-safe — forms one shared chain for all null-AccountId rows" requirement.
/// </para>
/// </remarks>
public static class AdvisoryLockKey
{
    /// <summary>
    /// Returns the 64-bit advisory-lock key for the given <see cref="Guid"/>, or
    /// <see langword="0"/> for a <see langword="null"/> input.
    /// </summary>
    /// <param name="id">The GUID to reduce to a lock key. <see langword="null"/> returns
    /// <see langword="0"/>.</param>
    /// <returns>An XOR of the two 64-bit halves of the GUID, or <see langword="0"/> when
    /// <paramref name="id"/> is <see langword="null"/>.</returns>
    public static long From(Guid? id)
    {
        if (!id.HasValue)
        {
            return 0L;
        }

        var bytes = id.Value.ToByteArray();
        return BitConverter.ToInt64(bytes, 0) ^ BitConverter.ToInt64(bytes, 8);
    }
}
