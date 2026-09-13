using System.ComponentModel.DataAnnotations;

namespace Storporate.Infrastructure.Storage;

/// <summary>
/// Provider-agnostic artifact/blob storage configuration, bound to the "ArtifactStorage"
/// section. Shaped so that swapping <see cref="Provider"/>/<see cref="Endpoint"/>/keys/bucket
/// from local MinIO to real Cloudflare R2 later is a config-value change only — no code change
/// (both are S3-compatible endpoints; see the plan's DEVIATION note on MinIO standing in for R2).
/// </summary>
public sealed class ArtifactStorageOptions
{
    public const string SectionName = "ArtifactStorage";

    /// <summary>E.g. "MinIO" or "CloudflareR2" — informational, used for logging/diagnostics.</summary>
    [Required]
    public string Provider { get; init; } = string.Empty;

    /// <summary>The S3-compatible service endpoint URL.</summary>
    [Required]
    public string Endpoint { get; init; } = string.Empty;

    [Required]
    public string BucketName { get; init; } = string.Empty;

    [Required]
    public string AccessKey { get; init; } = string.Empty;

    [Required]
    public string SecretKey { get; init; } = string.Empty;
}
