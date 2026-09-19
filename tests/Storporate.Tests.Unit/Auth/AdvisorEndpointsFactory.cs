using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Storporate.Infrastructure.Auditing;
using Storporate.SharedKernel.Abstractions;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.Auth;

/// <summary>
/// <see cref="AuthEndpointsFactory"/> derivative that ALSO swaps
/// <see cref="ILlmClient"/> for <see cref="FakeLlmClient"/> so advisor
/// processor / endpoint tests can drive the LLM with deterministic
/// canned responses and inspect every recorded call, AND swaps
/// <see cref="IAuditLogWriter"/> for <see cref="FakeAuditLogWriter"/> so
/// advisor endpoint tests can assert audit metadata ids + counts without
/// hitting the production Postgres-backed writer.
///
/// Everything else — in-memory EF provider, placeholder Options values,
/// ambient account context, fake artifact store — is inherited unchanged
/// from <see cref="AuthEndpointsFactory"/>. Tests that exercise only the
/// request pipeline (validators, 4xx error codes, cross-account 404s) can
/// stay on the parent factory; tests that need the LLM-to-database
/// pipeline to actually run resolve <see cref="FakeLlmClient"/> via
/// <c>_factory.Services.GetRequiredService&lt;FakeLlmClient&gt;()</c>.
/// </summary>
public sealed class AdvisorEndpointsFactory : AuthEndpointsFactory
{
    public FakeLlmClient LlmClient { get; } = new();
    public FakeAuditLogWriter AuditLogWriter { get; } = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // Storporate.Infrastructure.Llm registers ILlmClient as a
            // singleton service. Doing RemoveAll<ILlmClient>() first
            // guarantees the production BionicLlmProvider is gone before
            // our fake is registered, so tests can never accidentally hit
            // localhost:1234 (no real LLM listening during CI).
            services.RemoveAll<ILlmClient>();
            services.AddSingleton<ILlmClient>(_ => LlmClient);

            // IAuditLogWriter is registered as Scoped by
            // SecurityGovernance/DependencyInjection.cs. Replace it with
            // the singleton fake so advisor endpoint tests can resolve
            // the same instance via _factory.Services and inspect
            // Recorded entries without going through the (no-op under
            // InMemory) production Postgres path.
            services.RemoveAll<IAuditLogWriter>();
            services.AddSingleton<IAuditLogWriter>(_ => AuditLogWriter);
            // Also register the concrete type so the advisor endpoint
            // tests can resolve FakeAuditLogWriter directly. xUnit
            // materializes the host lazily, so the first test that
            // touches `_factory.Services` here triggers CreateHost.
            services.AddSingleton(_ => AuditLogWriter);
        });

        return base.CreateHost(builder);
    }
}
