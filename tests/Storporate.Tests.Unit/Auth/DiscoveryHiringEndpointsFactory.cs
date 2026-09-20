using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Storporate.Infrastructure.Auditing;
using Storporate.SharedKernel.Abstractions;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.Auth;

/// <summary>
/// <see cref="AuthEndpointsFactory"/> derivative for STOR-43 Phase 1
/// DiscoveryHiring endpoint tests. Swaps <see cref="IEmbeddingClient"/> for
/// <see cref="FakeEmbeddingClient"/> so no real LM Studio / OpenAI-compatible
/// endpoint is required when the refresh processor happens to run during the
/// endpoint test (it shouldn't — these tests target the synchronous request
/// pipeline — but the swap removes any chance of an accidental LLM call),
/// and swaps <see cref="IAuditLogWriter"/> for <see cref="FakeAuditLogWriter"/>
/// so the test can inspect "searchable_profile_enabled" /
/// "searchable_profile_disabled" rows directly.
///
/// Everything else — in-memory EF provider, placeholder Options values,
/// ambient account context, fake artifact store — is inherited unchanged.
/// </summary>
public sealed class DiscoveryHiringEndpointsFactory : AuthEndpointsFactory
{
    public FakeEmbeddingClient EmbeddingClient { get; } = new();
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