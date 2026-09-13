using System.Text;
using Storporate.SharedKernel.Storage;

namespace Storporate.Modules.PlatformFoundations.Diagnostics;

/// <summary>
/// Round-trips a throwaway object through <see cref="IArtifactStore"/> (put, get, delete, confirm
/// gone) to prove the artifact storage wiring works end-to-end. Temporary diagnostic only (see
/// <c>GET /api/diagnostics/storage-ping</c> in <c>Program.cs</c>).
/// </summary>
public static class StoragePingHandler
{
    private const string TestContent = "storporate-storage-ping";

    public static async Task<StoragePingResult> ExecuteAsync(
        IArtifactStore artifactStore,
        CancellationToken cancellationToken)
    {
        var key = $"diagnostics/ping-{Guid.NewGuid()}.txt";
        var testBytes = Encoding.UTF8.GetBytes(TestContent);

        await using (var uploadStream = new MemoryStream(testBytes))
        {
            await artifactStore.PutAsync(key, uploadStream, "text/plain", cancellationToken);
        }
        var uploadSucceeded = true;

        var downloadContentMatched = false;
        var downloaded = await artifactStore.GetAsync(key, cancellationToken);
        if (downloaded is not null)
        {
            await using (downloaded.Content)
            {
                using var buffer = new MemoryStream();
                await downloaded.Content.CopyToAsync(buffer, cancellationToken);
                downloadContentMatched = buffer.ToArray().AsSpan().SequenceEqual(testBytes);
            }
        }

        await artifactStore.DeleteAsync(key, cancellationToken);
        var deleteSucceeded = true;

        var postDeleteObject = await artifactStore.GetAsync(key, cancellationToken);
        var postDeleteNotFoundConfirmed = postDeleteObject is null;
        if (postDeleteObject is not null)
        {
            await postDeleteObject.Content.DisposeAsync();
        }

        return new StoragePingResult(
            key,
            uploadSucceeded,
            downloadContentMatched,
            deleteSucceeded,
            postDeleteNotFoundConfirmed);
    }
}

/// <summary>Outcome of a single <see cref="StoragePingHandler"/> run.</summary>
public sealed record StoragePingResult(
    string Key,
    bool UploadSucceeded,
    bool DownloadContentMatched,
    bool DeleteSucceeded,
    bool PostDeleteNotFoundConfirmed);
