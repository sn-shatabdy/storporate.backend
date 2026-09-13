using System.Security.Cryptography;
using System.Text;
using Storporate.SharedKernel.Security;

namespace Storporate.Tests.Unit.Security;

public class AesGcmFieldEncryptorTests
{
    private const string ValidBase64Key = "CC40PEtQJ3kOFRSJA0Hz40p4pgahPMvxLdvoFIDPyno=";

    [Fact]
    public void EncryptThenDecrypt_ReturnsOriginalPlaintextByteForByte()
    {
        var plaintext = Encoding.UTF8.GetBytes("a sensitive field value");

        var envelope = AesGcmFieldEncryptor.Encrypt(ValidBase64Key, plaintext);
        var decrypted = AesGcmFieldEncryptor.Decrypt(ValidBase64Key, envelope);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void EncryptThenDecrypt_WithAssociatedData_ReturnsOriginalPlaintextByteForByte()
    {
        var plaintext = Encoding.UTF8.GetBytes("another sensitive field value");
        var associatedData = Encoding.UTF8.GetBytes("field-name-context");

        var envelope = AesGcmFieldEncryptor.Encrypt(ValidBase64Key, plaintext, associatedData);
        var decrypted = AesGcmFieldEncryptor.Decrypt(ValidBase64Key, envelope, associatedData);

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Decrypt_WithTamperedEnvelope_ThrowsCryptographicException()
    {
        var plaintext = Encoding.UTF8.GetBytes("a sensitive field value");
        var envelope = AesGcmFieldEncryptor.Encrypt(ValidBase64Key, plaintext);

        // Flip one bit in the middle of the envelope (inside the ciphertext region) so the
        // GCM auth tag no longer validates — proves the tag is actually checked on decrypt.
        envelope[envelope.Length / 2] ^= 0xFF;

        // .NET 10 throws the more specific AuthenticationTagMismatchException, which derives
        // from CryptographicException — ThrowsAny accepts the base type or any subclass.
        Assert.ThrowsAny<CryptographicException>(() => AesGcmFieldEncryptor.Decrypt(ValidBase64Key, envelope));
    }

    [Theory]
    [InlineData("not-valid-base64!!!")]
    [InlineData("dG9vc2hvcnQ=")] // valid base64, decodes to far fewer than 32 bytes
    public void Encrypt_WithInvalidKeyLength_ThrowsArgumentException(string invalidKey)
    {
        var plaintext = Encoding.UTF8.GetBytes("a sensitive field value");

        Assert.Throws<ArgumentException>(() => AesGcmFieldEncryptor.Encrypt(invalidKey, plaintext));
    }

    [Fact]
    public void IsValidKey_WithValidAes256Key_ReturnsTrue()
    {
        Assert.True(AesGcmFieldEncryptor.IsValidKey(ValidBase64Key));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-valid-base64!!!")]
    [InlineData("dG9vc2hvcnQ=")] // valid base64, decodes to far fewer than 32 bytes
    public void IsValidKey_WithInvalidKey_ReturnsFalse(string invalidKey)
    {
        Assert.False(AesGcmFieldEncryptor.IsValidKey(invalidKey));
    }
}
