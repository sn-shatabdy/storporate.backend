using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Storporate.Infrastructure.Auditing;
using Storporate.SharedKernel.Abstractions;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.Auth;

/// <summary>
/// <see cref="AuthEndpointsFactory"/> derivative for STOR-43 DiscoveryHiring
/// endpoint tests (Phase 1 + Phase 2). Swaps <see cref="IEmbeddingClient"/>
/// and <see cref="ILlmClient"/> for the recording fakes so any processor that
/// happens to run during the endpoint test (refresh + search) never reaches
/// out to localhost:1234, and swaps <see cref="IAuditLogWriter"/> for
/// <see cref="FakeAuditLogWriter"/> so the test can inspect
/// <c>searchable_profile_*</c> / <c>talent_search_*</c> audit rows directly.
///
/// Everything else — in-memory EF provider, placeholder Options values,
/// ambient account context, fake artifact store — is inherited unchanged.
/// </summary>
public sealed class DiscoveryHiringEndpointsFactory : AuthEndpointsFactory
{
    public FakeEmbeddingClient EmbeddingClient { get; } = new();
    public FakeLlmClient LlmClient { get; } = new();
    public FakeAuditLogWriter AuditLogWriter { get; } = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // IEmbeddingClient is registered as Transient by
            // LlmServiceCollectionExtensions.AddBionicEmbeddings. Remove the
            // production BionicEmbeddingClient and install the fake so the
            // refresh processor (if exercised) gets deterministic
            // embeddings without touching localhost:1234.
            services.RemoveAll<IEmbeddingClient>();
            services.AddSingleton(_ => EmbeddingClient);
            services.AddTransient<IEmbeddingClient>(_ => EmbeddingClient);

            // Phase 2: SearchTalentJobProcessor also depends on ILlmClient.
            // RemoveAll + re-register the singleton fake so endpoint tests
            // that drive the search path see deterministic canned
            // responses (and so the processor's retrieval → ranking
            // pipeline can be exercised end-to-end without an LLM call).
            services.RemoveAll<ILlmClient>();
            services.AddSingleton<ILlmClient>(_ => LlmClient);

            // IAuditLogWriter is registered as Scoped by
            // SecurityGovernance/DependencyInjection.cs. Replace it with the
            // singleton fake so DiscoveryHiring endpoint tests can resolve
            // the same instance via _factory.Services and inspect
            // Recorded entries without going through the (no-op under
            // InMemory) production Postgres path.
            services.RemoveAll<IAuditLogWriter>();
            services.AddSingleton<IAuditLogWriter>(_ => AuditLogWriter);
            services.AddSingleton(_ => AuditLogWriter);
        });

        return base.CreateHost(builder);
    }
}