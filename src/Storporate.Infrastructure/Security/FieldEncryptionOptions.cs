using System.ComponentModel.DataAnnotations;

namespace Storporate.Infrastructure.Security;

/// <summary>
/// Configuration for <see cref="Storporate.SharedKernel.Security.AesGcmFieldEncryptor"/>, bound
/// to the "Encryption" section (env var <c>Encryption__FieldEncryptionKey</c>).
/// <see cref="FieldEncryptionKey"/> must be a base64-encoded 32-byte (AES-256) key — enforced
/// both here (presence) and by a custom validator registered alongside this options binding in
/// Program.cs (decoded length), matching the fail-fast pattern already used for
/// <see cref="Storporate.Infrastructure.Storage.ArtifactStorageOptions"/> and
/// <see cref="Storporate.Infrastructure.Llm.BionicOptions"/>.
/// </summary>
public sealed class FieldEncryptionOptions
{
    public const string SectionName = "Encryption";

    [Required]
    public string FieldEncryptionKey { get; init; } = string.Empty;
}
