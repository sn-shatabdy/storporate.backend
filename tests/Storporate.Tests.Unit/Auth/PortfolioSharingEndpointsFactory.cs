using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Storporate.Infrastructure.Auditing;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.Auth;

/// <summary>
/// <see cref="AuthEndpointsFactory"/> derivative for STOR-44 Phase 1
/// <c>PUT /api/portfolio/items/{id}/sharing</c> endpoint tests. Swaps
/// <see cref="IAuditLogWriter"/> for <see cref="FakeAuditLogWriter"/> so
/// the test can inspect the <c>portfolio_sharing_changed</c> audit row
/// directly — the production Postgres writer is a no-op under the
/// InMemory provider the unit suite uses.
/// </summary>
public sealed class PortfolioSharingEndpointsFactory : AuthEndpointsFactory
{
    public FakeAuditLogWriter AuditLogWriter { get; } = new();

    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAuditLogWriter>();
            services.AddSingleton<IAuditLogWriter>(_ => AuditLogWriter);
        });

        return base.CreateHost(builder);
    }
}
