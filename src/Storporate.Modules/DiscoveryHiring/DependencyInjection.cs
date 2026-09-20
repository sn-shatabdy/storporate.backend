using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Jobs;
using Storporate.Modules.DiscoveryHiring.TalentIndex;
using Storporate.Modules.DiscoveryHiring.TalentSearch;

namespace Storporate.Modules.DiscoveryHiring;

/// <summary>
/// DI entry point for the DiscoveryHiring module. Phase 1 wires only the
/// <see cref="RefreshTalentIndexEntryProcessor"/> background-job processor;
/// Phase 2 adds the <see cref="SearchTalentJobProcessor"/> under the same
/// <see cref="IBackgroundJobProcessor"/> shape so the worker picks it up
/// via the same dispatch list.
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
/// factory cheap. Phase 2's <see cref="SearchTalentJobProcessor"/> slots
/// in beside the refresh processor under the same pattern.
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
        // Scoped: each processor holds a WriteDbContext and an
        // ITalentIndexRepository, both scoped. The worker's
        // IServiceScopeFactory creates a fresh scope per tick and resolves
        // every IBackgroundJobProcessor from it.
        services.AddScoped<RefreshTalentIndexEntryProcessor>();
        services.AddScoped<IBackgroundJobProcessor>(sp =>
            sp.GetRequiredService<RefreshTalentIndexEntryProcessor>());

        services.AddScoped<SearchTalentJobProcessor>();
        services.AddScoped<IBackgroundJobProcessor>(sp =>
            sp.GetRequiredService<SearchTalentJobProcessor>());

        return services;
    }
}
