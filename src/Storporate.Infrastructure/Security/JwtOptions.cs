using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Storporate.Infrastructure.Security;

/// <summary>
/// Configuration for <see cref="JwtTokenService"/>, bound to the "Jwt" section (env vars
/// <c>Jwt__SigningKey</c>/<c>Jwt__Issuer</c>/<c>Jwt__Audience</c>/...). <see cref="SigningKey"/>
/// must be long enough for HMAC-SHA256 (at least 32 bytes / 256 bits) — enforced both here
/// (presence) and by a custom validator registered alongside this options binding in Program.cs
/// (decoded length), matching the fail-fast pattern already used for
/// <see cref="FieldEncryptionOptions"/>/<see cref="Storporate.Infrastructure.Storage.ArtifactStorageOptions"/>.
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>Minimum signing-key length in bytes required for HMAC-SHA256 (256 bits).</summary>
    public const int MinimumSigningKeyLengthBytes = 32;

    [Required]
    public string SigningKey { get; init; } = string.Empty;

    [Required]
    public string Issuer { get; init; } = string.Empty;

    [Required]
    public string Audience { get; init; } = string.Empty;

    [Range(1, int.MaxValue)]
    public int AccessTokenMinutes { get; init; } = 15;

    [Range(1, int.MaxValue)]
    public int RefreshTokenDays { get; init; } = 7;

    /// <summary>True when <paramref name="signingKey"/> is long enough (UTF-8 byte count) to be a
    /// secure HMAC-SHA256 key. Mirrors <c>AesGcmFieldEncryptor.IsValidKey</c>'s role for
    /// <see cref="FieldEncryptionOptions"/>: the presence check lives on the property via
    /// <see cref="RequiredAttribute"/>, this length check is wired in separately via
    /// <c>OptionsBuilder.Validate</c> in Program.cs.</summary>
    public static bool IsValidSigningKey(string signingKey) =>
        !string.IsNullOrWhiteSpace(signingKey)
        && Encoding.UTF8.GetByteCount(signingKey) >= MinimumSigningKeyLengthBytes;
}
