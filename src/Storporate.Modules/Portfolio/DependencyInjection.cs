using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Jobs;
using Storporate.Modules.Portfolio.Analysis;

namespace Storporate.Modules.Portfolio;

/// <summary>
/// DI entry point for the Portfolio module. STOR-37 Phase 1: there are no
/// scoped registrations of our own — FluentValidation validators are discovered by
/// the assembly-wide <c>AddValidatorsFromAssembly(...)</c> call in
/// <c>Program.cs</c> (same pattern <see cref="Storporate.Modules.Identity.DependencyInjection"/>
/// documents), and the handler logic lives as <c>static</c> methods that consume
/// <see cref="Storporate.Infrastructure.Persistence.WriteDbContext"/> /
/// <see cref="Storporate.SharedKernel.Storage.IArtifactStore"/> directly from the
/// request scope. A future story that adds scoped handler-internal state would
/// extend this class rather than inventing a new wiring pattern.
/// </summary>
/// <remarks>
/// <b>STOR-38 Phase 2.</b> The analysis worker introduces the module's first
/// scoped services (<see cref="EvidenceContentExtractor"/> +
/// <see cref="PortfolioAnalysisJobProcessor"/>). Both are scoped because they
/// hold a reference to <see cref="Storporate.Infrastructure.Persistence.WriteDbContext"/>:
/// every tick of the worker builds a fresh scope via <c>IServiceScopeFactory</c>
/// and resolves the processor from it, so the per-job <c>DbContext</c> lifetime
/// matches the per-job lifetime exactly.
/// </remarks>
public static class DependencyInjection
{
    public static IServiceCollection AddPortfolioHandlers(this IServiceCollection services)
    {
        services.AddScoped<EvidenceContentExtractor>();
        // Register the concrete processor as itself (for direct test/handler
        // use) AND as IBackgroundJobProcessor (for the polling worker in
        // Storporate.Infrastructure/Jobs). The worker's GetServices<IBackgroundJobProcessor>
        // call resolves the latter, so the worker doesn't need a reference back
        // to this module — it stays in Infrastructure where the established
        // layering rule "Infrastructure has no reference back to Modules" allows it.
        services.AddScoped<PortfolioAnalysisJobProcessor>();
        services.AddScoped<IBackgroundJobProcessor>(sp =>
            sp.GetRequiredService<PortfolioAnalysisJobProcessor>());
        return services;
    }
}