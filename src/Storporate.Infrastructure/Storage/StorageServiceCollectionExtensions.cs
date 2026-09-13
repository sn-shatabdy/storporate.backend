using Amazon.S3;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Storporate.SharedKernel.Storage;

namespace Storporate.Infrastructure.Storage;

/// <summary>DI entry point wiring the S3-compatible artifact storage backend.</summary>
public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="IAmazonS3"/> configured against <see cref="ArtifactStorageOptions"/>
    /// — <c>ServiceURL</c> pointed at the configured endpoint and <c>ForcePathStyle</c> enabled,
    /// which is what makes this client work against any S3-compatible endpoint (local MinIO
    /// today, real Cloudflare R2 later) rather than only real AWS S3 — plus
    /// <see cref="IArtifactStore"/> as <see cref="S3ArtifactStore"/>. Assumes
    /// <see cref="ArtifactStorageOptions"/> is already bound and validated (see the
    /// Options-pattern registration in <c>Program.cs</c>).
    /// </summary>
    public static IServiceCollection AddArtifactStorage(this IServiceCollection services)
    {
        services.AddSingleton<IAmazonS3>(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<ArtifactStorageOptions>>().Value;

            var config = new AmazonS3Config
            {
                ServiceURL = options.Endpoint,
                ForcePathStyle = true,
            };

            return new AmazonS3Client(options.AccessKey, options.SecretKey, config);
        });

        services.AddSingleton<IArtifactStore, S3ArtifactStore>();

        return services;
    }
}
