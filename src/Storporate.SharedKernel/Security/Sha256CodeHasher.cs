using System.Security.Cryptography;
using System.Text;

namespace Storporate.SharedKernel.Security;

/// <summary>
/// One-way SHA-256 hasher for values that must be verified but never recovered — OTP codes and
/// refresh tokens. Deterministic (the same input always produces the same output), so a
/// presented value can be hashed and compared against the stored hash, but never reversible.
/// This is deliberately a different tool from <see cref="AesGcmFieldEncryptor"/>: that encryptor
/// is reversible envelope encryption for data that must be read back in plaintext later, which is
/// the wrong shape for OTP codes and refresh tokens (only equality-checking is ever needed, and
/// the plaintext must never be recoverable from storage, even by the application itself).
/// </summary>
public static class Sha256CodeHasher
{
    public static string Hash(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var hashBytes = SHA256.HashData(bytes);
        return Convert.ToHexString(hashBytes);
    }
}
