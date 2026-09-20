using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Llm;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.TalentIndex;

/// <summary>
/// STOR-43 Phase 1: pin the wire payload + reorder/count/dimension guards of
/// the <see cref="BionicEmbeddingClient"/>. Mirrors the
/// <c>BionicLlmProviderHistoryTests</c> pattern — drive the real provider
/// through a capturing <see cref="HttpMessageHandler"/> and assert the
/// serialized request body and the deserialized response handling.
/// </summary>
public class BionicEmbeddingClientTests
{
    private const string ModelId = "text-embedding-nomic-embed-text-v1.5";

    [Fact]
    public async Task EmbedAsync_SendsPostEmbeddingsWithModelAndInputArray()
    {
        var recorder = await SendAsync(
            inputs: new[] { "alpha", "beta" },
            responseRows: new[]
            {
                (Index: 0, Vector: MakeVector(0.1f, 0.2f)),
                (Index: 1, Vector: MakeVector(0.3f, 0.4f)),
            });

        Assert.Equal(new Uri("http://localhost:1234/v1/embeddings"), recorder.CapturedUri);

        Assert.NotNull(recorder.CapturedBody);
        using var document = JsonDocument.Parse(recorder.CapturedBody!);
        var root = document.RootElement;
        Assert.Equal(ModelId, root.GetProperty("model").GetString());
        var input = root.GetProperty("input").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "alpha", "beta" }, input);
    }

    [Fact]
    public async Task EmbedAsync_ReordersRowsByIndexField()
    {
        // Provider returns rows out of order; the client must re-sort so
        // caller's results[i] matches inputs[i].
        var recorder = await SendAsync(
            inputs: new[] { "a", "b", "c" },
            responseRows: new[]
            {
                (Index: 2, Vector: MakeVector(0.5f, 0.6f)),
                (Index: 0, Vector: MakeVector(0.1f, 0.2f)),
                (Index: 1, Vector: MakeVector(0.3f, 0.4f)),
            },
            returnOrderedRows: false);

        var vectors = recorder.ReturnedVectors;
        Assert.Equal(3, vectors.Length);
        // Position 0 must correspond to input "a" — which the server
        // returned with index=0.
        Assert.Equal(0.1f, vectors[0][0]);
        Assert.Equal(0.3f, vectors[1][0]);
        Assert.Equal(0.5f, vectors[2][0]);
    }

    [Fact]
    public async Task EmbedAsync_RowCountMismatch_ThrowsLlmProviderException()
    {
        // Server returns 1 row but we asked for 2 — the client must throw
        // before any DB write happens (the unit test asserts the exception
        // type; no DB is touched here).
        var recorder = await SendAsync(
            inputs: new[] { "alpha", "beta" },
            responseRows: new[]
            {
                (Index: 0, Vector: MakeVector(0.1f, 0.2f)),
            });

        Assert.IsType<LlmProviderException>(recorder.ThrownException);
        Assert.Contains("1 row", recorder.ThrownException!.Message);
        Assert.Contains("expected 2", recorder.ThrownException.Message);
    }

    [Fact]
    public async Task EmbedAsync_DimensionMismatch_ThrowsLlmProviderException()
    {
        // Server returns a 2-dimensional vector when the configured
        // dimension is 768 — the client must throw before any DB write.
        var recorder = await SendAsync(
            inputs: new[] { "alpha" },
            responseRows: new[]
            {
                (Index: 0, Vector: new float[] { 0.1f, 0.2f }),
            });

        Assert.IsType<LlmProviderException>(recorder.ThrownException);
        Assert.Contains("vector of length 2", recorder.ThrownException!.Message);
        Assert.Contains("expected 768", recorder.ThrownException.Message);
    }

    [Fact]
    public async Task EmbedAsync_EmptyInputs_ReturnsEmptyWithoutHttpCall()
    {
        // No inputs means no embedding work; the client short-circuits to
        // an empty array without ever opening a request. Useful for callers
        // (the refresh processor's "no items found" branch) that pass an
        // empty batch.
        var recorder = await SendAsync(inputs: Array.Empty<string>(), responseRows: Array.Empty<(int, float[])>());

        Assert.Null(recorder.CapturedUri);
        Assert.Empty(recorder.ReturnedVectors);
    }

    [Fact]
    public async Task EmbedAsync_ChunksAcrossMaxBatchSizeBoundary()
    {
        // The MaxBatchSize=16 cap means a 20-input batch triggers two
        // /embeddings requests. The unit test asserts the client's batch-
        // chunking rather than the response ordering (already covered above)
        // because the more interesting failure mode is "one request per
        // batch, never more" — a bug that would saturate the upstream quota
        // for large batches.
        var inputs = Enumerable.Range(0, 20).Select(i => $"input-{i}").ToArray();
        var rows = inputs
            .Select((_, i) => (Index: i, Vector: MakeVector(0.0f, 0.0f)))
            .ToArray();

        var recorder = await SendAsync(
            inputs: inputs,
            responseRows: rows,
            maxBatchSize: 16);

        Assert.Equal(2, recorder.RequestCount);
        // First batch: 16 rows. Second batch: 4 rows.
        Assert.Equal(16, recorder.BatchSizes[0]);
        Assert.Equal(4, recorder.BatchSizes[1]);
    }

    private static float[] MakeVector(float a, float b) =>
        Enumerable.Range(0, TalentIndexConstants.EmbeddingDimensions)
            .Select(i => i == 0 ? a : i == 1 ? b : 0f)
            .ToArray();

    /// <summary>
    /// Build a <see cref="BionicEmbeddingClient"/> wired to a capturing
    /// <see cref="HttpMessageHandler"/>, drive one <c>EmbedAsync</c> call,
    /// and return the recorded state.
    /// </summary>
    private static async Task<RecordingHandler> SendAsync(
        IReadOnlyList<string> inputs,
        IReadOnlyList<(int Index, float[] Vector)> responseRows,
        int maxBatchSize = 16,
        bool returnOrderedRows = true)
    {
        var recorder = new RecordingHandler(
            rowsByRequest: SplitByBatchSize(responseRows, maxBatchSize),
            returnOrderedRows: returnOrderedRows);

        var services = new ServiceCollection();
        services.AddSingleton(recorder);
        services.AddHttpClient(LlmHttpClientNames.Bionic, httpClient =>
        {
            httpClient.BaseAddress = new Uri("http://localhost:1234/v1/");
            httpClient.Timeout = TimeSpan.FromSeconds(60);
        })
        .ConfigurePrimaryHttpMessageHandler(() => recorder);
        services.AddSingleton(Options.Create(new BionicOptions
        {
            BaseUrl = "http://localhost:1234/v1",
            ModelId = ModelId,
        }));
        services.AddSingleton(Options.Create(new EmbeddingOptions
        {
            BaseUrl = string.Empty, // share the Bionic client
            ModelId = ModelId,
            Dimensions = TalentIndexConstants.EmbeddingDimensions,
            MaxBatchSize = maxBatchSize,
        }));
        services.AddTransient<IEmbeddingClient, BionicEmbeddingClient>();

        var sp = services.BuildServiceProvider();
        var client = sp.GetRequiredService<IEmbeddingClient>();

        try
        {
            var vectors = await client.EmbedAsync(inputs, CancellationToken.None);
            recorder.ReturnedVectors = vectors;
        }
        catch (Exception ex)
        {
            recorder.ThrownException = ex;
        }

        return recorder;
    }

    private static List<IReadOnlyList<(int Index, float[] Vector)>> SplitByBatchSize(
        IReadOnlyList<(int Index, float[] Vector)> rows,
        int maxBatchSize)
    {
        if (rows.Count == 0)
        {
            return new List<IReadOnlyList<(int Index, float[] Vector)>>();
        }

        var batches = new List<IReadOnlyList<(int Index, float[] Vector)>>();
        for (var start = 0; start < rows.Count; start += maxBatchSize)
        {
            var end = Math.Min(start + maxBatchSize, rows.Count);
            var batch = rows.Skip(start).Take(end - start).ToArray();
            batches.Add(batch);
        }
        return batches;
    }

    /// <summary>
    /// Minimal <see cref="HttpMessageHandler"/> that records one body
    /// per request and returns the pre-built response for that batch.
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Queue<IReadOnlyList<(int Index, float[] Vector)>> _pendingBatches;
        private readonly bool _returnOrderedRows;

        public RecordingHandler(
            List<IReadOnlyList<(int Index, float[] Vector)>> rowsByRequest,
            bool returnOrderedRows)
        {
            _pendingBatches = new Queue<IReadOnlyList<(int Index, float[] Vector)>>(rowsByRequest);
            _returnOrderedRows = returnOrderedRows;
        }

        public string? CapturedBody { get; private set; }
        public Uri? CapturedUri { get; private set; }
        public int RequestCount { get; private set; }
        public List<int> BatchSizes { get; } = new();
        public float[][] ReturnedVectors { get; set; } = Array.Empty<float[]>();
        public Exception? ThrownException { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            CapturedUri = request.RequestUri;
            if (request.Content is not null)
            {
                CapturedBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            if (_pendingBatches.Count == 0)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("no more queued response batches"),
                };
            }

            var rows = _pendingBatches.Dequeue();
            BatchSizes.Add(rows.Count);

            var orderedRows = _returnOrderedRows
                ? rows.OrderBy(r => r.Index).ToArray()
                : rows.ToArray();

            var dataJson = string.Join(",", orderedRows.Select(r =>
                $$"""{"index":{{r.Index}},"embedding":[{{string.Join(",", r.Vector.Select(v => v.ToString(System.Globalization.CultureInfo.InvariantCulture)))}}]}"""));

            var json = $$"""{"data": [{{dataJson}}]}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json),
            };
        }
    }
}
