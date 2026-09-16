using Storporate.SharedKernel.Persistence;

namespace Storporate.Tests.Unit.Persistence;

/// <summary>
/// Pins the <see cref="AdvisoryLockKey"/> reduction contract — the writer relies on
/// <see cref="AdvisoryLockKey.From"/> to map an account id (or a null for the shared
/// pre-account chain) to a Postgres advisory-lock key, so any drift here changes which rows
/// serialize against each other.
/// </summary>
public class AdvisoryLockKeyTests
{
    [Fact]
    public void From_Null_ReturnsZero()
    {
        // All pre-account audit rows share one chain, and that chain's lock key is
        // defined to be 0 (the XOR-of-nothing value). Pinning this explicitly so a future
        // refactor doesn't accidentally start producing a non-deterministic key for null.
        Assert.Equal(0L, AdvisoryLockKey.From(null));
    }

    [Fact]
    public void From_TwoDistinctGuids_ProduceDistinctKeys()
    {
        var a = new Guid("11111111-2222-3333-4444-555555555555");
        var b = new Guid("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

        var keyA = AdvisoryLockKey.From(a);
        var keyB = AdvisoryLockKey.From(b);

        Assert.NotEqual(keyA, keyB);
    }

    [Fact]
    public void From_SameGuidTwice_ProducesSameKey()
    {
        var id = new Guid("12345678-90ab-cdef-1234-567890abcdef");

        var first = AdvisoryLockKey.From(id);
        var second = AdvisoryLockKey.From(id);

        Assert.Equal(first, second);
    }

    [Fact]
    public void From_HighAndLowHalvesBothContributeToResult()
    {
        // XOR is symmetric, so swapping the two 64-bit halves of a GUID produces the
        // same XOR key (a property we can verify but not test in the "different"
        // direction). The property we *can* pin: the XOR reduction must use both halves
        // — a regression that read only the first 8 bytes or only the last 8 bytes would
        // produce a key derived from a single half. Construct two GUIDs whose first
        // 8 bytes are identical but last 8 bytes differ, and confirm the keys differ;
        // likewise for identical last 8 bytes with different first 8 bytes. Each
        // direction proves that the matching half matters.
        var firstHighSameLowDiff = new Guid(new byte[]
        {
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, // high
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, // low
        });
        var secondHighSameLowDiff = new Guid(new byte[]
        {
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, // high
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x03, // low
        });
        var thirdHighDiffLowSame = new Guid(new byte[]
        {
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04, // high
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, // low
        });

        var keyA = AdvisoryLockKey.From(firstHighSameLowDiff);
        var keyB = AdvisoryLockKey.From(secondHighSameLowDiff);
        var keyC = AdvisoryLockKey.From(thirdHighDiffLowSame);

        // Differing low halves must produce differing keys — proves the low half is read.
        Assert.NotEqual(keyA, keyB);
        // Differing high halves (with same low) must produce differing keys — proves the
        // high half is read too.
        Assert.NotEqual(keyA, keyC);
    }
}
