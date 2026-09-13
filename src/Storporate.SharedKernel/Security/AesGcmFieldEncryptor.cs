using System.Security.Cryptography;

namespace Storporate.SharedKernel.Security;

/// <summary>
/// Static AES-256-GCM envelope encryptor for individual sensitive field values. Ported from
/// Docomate's proven <c>ConnectorCredentialEncryptor</c> (production use there since its own
/// first sensitive field). Not yet wired to a real field in Storporate — Authentication or the
/// Evidence Engine will be the first consumer once either introduces a sensitive value.
/// Envelope shape: 12-byte nonce || ciphertext || 16-byte tag, concatenated.
/// </summary>
public static class AesGcmFieldEncryptor
{
    private const int NonceSizeBytes = 12;
    private const int TagSizeBytes = 16;
    private const int KeySizeBytes = 32;

    /// <summary>
    /// Returns whether <paramref name="base64Key"/> decodes to a valid AES-256 key (exactly
    /// <see cref="KeySizeBytes"/> bytes). Used by options validation (e.g. <c>Program.cs</c>'s
    /// <c>FieldEncryptionOptions</c> registration) so key-shape validation has a single source
    /// of truth shared with <see cref="Encrypt"/>/<see cref="Decrypt"/> instead of being
    /// reimplemented at each call site.
    /// </summary>
    public static bool IsValidKey(string base64Key)
    {
        try
        {
            DecodeKey(base64Key);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static byte[] Encrypt(string base64Key, byte[] plaintext, byte[]? associatedData = null)
    {
        var key = DecodeKey(base64Key);

        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        using var aesGcm = new AesGcm(key, TagSizeBytes);
        aesGcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);

        var envelope = new byte[NonceSizeBytes + ciphertext.Length + TagSizeBytes];
        nonce.CopyTo(envelope, 0);
        ciphertext.CopyTo(envelope, NonceSizeBytes);
        tag.CopyTo(envelope, NonceSizeBytes + ciphertext.Length);
        return envelope;
    }

    public static byte[] Decrypt(string base64Key, byte[] envelope, byte[]? associatedData = null)
    {
        var key = DecodeKey(base64Key);

        if (envelope.Length < NonceSizeBytes + TagSizeBytes)
        {
            throw new ArgumentException(
                "Envelope is too short to contain a nonce and tag.", nameof(envelope));
        }

        var nonce = envelope.AsSpan(0, NonceSizeBytes);
        var ciphertextLength = envelope.Length - NonceSizeBytes - TagSizeBytes;
        var ciphertext = envelope.AsSpan(NonceSizeBytes, ciphertextLength);
        var tag = envelope.AsSpan(NonceSizeBytes + ciphertextLength, TagSizeBytes);

        var plaintext = new byte[ciphertextLength];

        using var aesGcm = new AesGcm(key, TagSizeBytes);
        aesGcm.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);

        return plaintext;
    }

    private static byte[] DecodeKey(string base64Key)
    {
        byte[] key;
        try
        {
            key = Convert.FromBase64String(base64Key);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Key is not valid base64.", nameof(base64Key), ex);
        }

        if (key.Length != KeySizeBytes)
        {
            throw new ArgumentException(
                $"Key must decode to exactly {KeySizeBytes} bytes (AES-256), got {key.Length}.",
                nameof(base64Key));
        }

        return key;
    }
}
