using Storporate.SharedKernel.Security;

namespace Storporate.Tests.Unit.Security;

public class Sha256CodeHasherTests
{
    [Fact]
    public void Hash_CalledTwiceWithSameInput_ReturnsSameValue()
    {
        var first = Sha256CodeHasher.Hash("123456");
        var second = Sha256CodeHasher.Hash("123456");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Hash_WithDifferentInputs_ReturnsDifferentValues()
    {
        var hashOfCorrectCode = Sha256CodeHasher.Hash("123456");
        var hashOfDifferentCode = Sha256CodeHasher.Hash("654321");

        Assert.NotEqual(hashOfCorrectCode, hashOfDifferentCode);
    }

    [Fact]
    public void Hash_DoesNotReturnThePlaintextInput()
    {
        var hash = Sha256CodeHasher.Hash("123456");

        Assert.NotEqual("123456", hash);
    }
}
