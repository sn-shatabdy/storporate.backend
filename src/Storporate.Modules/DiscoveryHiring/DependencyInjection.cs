using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Jobs;
using Storporate.Modules.DiscoveryHiring.TalentIndex;

namespace Storporate.Modules.DiscoveryHiring;

/// <summary>
/// DI entry point for the DiscoveryHiring module. Phase 1 wires only the
/// <see cref="RefreshTalentIndexEntryProcessor"/> background-job processor;
/// Phase 2 will add the search-endpoint handler chain on top.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the processor is registered against
/// <see cref="IBackgroundJobProcessor"/> as well as the concrete type.</b>
/// The polling worker
/// (<c>Storporate.Infrastructure.Jobs.PortfolioAnalysisWorker</c>) resolves
/// <see cref="IEnumerable{IBackgroundJobProcessor}"/> per tick; without the
/// second registration the worker would never claim a
/// <see cref="TalentIndexJobTypes.RefreshEntry"/> job. Registering the
/// concrete type first and forwarding the interface keeps the per-tick
/// factory cheap and lets a future phase add the search-endpoint processor
/// under the same pattern.
/// </para>
/// </remarks>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the module's processor services. Idempotent — every call
    /// adds the same registrations. Callers should invoke this once during
    /// host construction (see <c>Program.cs</c>'s
    /// <c>AddDiscoveryHiringHandlers()</c> call alongside the other modules).
    /// </summary>
    public static IServiceCollection AddDiscoveryHiringHandlers(this IServiceCollection services)
    {
        // Scoped: the processor holds a WriteDbContext and an
        // ITalentIndexRepository, both scoped. The worker's
        // IServiceScopeFactory creates a fresh scope per tick and resolves
        // the processor from it.
        services.AddScoped<RefreshTalentIndexEntryProcessor>();
        services.AddScoped<IBackgroundJobProcessor>(sp =>
            sp.GetRequiredService<RefreshTalentIndexEntryProcessor>());

        return services;
    }
}
