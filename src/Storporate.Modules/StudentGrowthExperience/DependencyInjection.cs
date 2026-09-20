using Microsoft.Extensions.DependencyInjection;
using Storporate.Infrastructure.Jobs;
using Storporate.Modules.StudentGrowthExperience.Advisor;

namespace Storporate.Modules.StudentGrowthExperience;

/// <summary>
/// DI entry point for the StudentGrowthExperience module. Phase 1 wired
/// only the module's options class; Phase 2 adds the two background
/// processors (advisor turn + compare explorations) and the
/// <see cref="AdvisorOptions"/> binding on top. The handlers themselves
/// are <c>static</c> methods resolved from the request scope — no per-
/// handler scoped registration needed, mirroring the Portfolio module's
/// pattern.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the module's options + processor services. Idempotent —
    /// every call adds the same registrations. Callers should invoke this
    /// once during host construction (see <c>Program.cs</c>'s
    /// <c>AddStudentGrowthExperienceHandlers()</c> call alongside the
    /// other modules).
    /// </summary>
    public static IServiceCollection AddStudentGrowthExperienceHandlers(this IServiceCollection services)
    {
        // STOR-40 Phase 2: the two background-job processors both hold a
        // reference to WriteDbContext so they have to be scoped (every
        // worker tick builds a fresh scope via IServiceScopeFactory and
        // resolves the processor from it). Registered as the concrete
        // type AND as IBackgroundJobProcessor — the polling worker's
        // GetServices<IBackgroundJobProcessor>() resolves the latter, so
        // the worker stays in Infrastructure and doesn't need a reference
        // back to this module.
        services.AddScoped<AdvisorTurnJobProcessor>();
        services.AddScoped<IBackgroundJobProcessor>(sp =>
            sp.GetRequiredService<AdvisorTurnJobProcessor>());

        services.AddScoped<CompareExplorationsJobProcessor>();
        services.AddScoped<IBackgroundJobProcessor>(sp =>
            sp.GetRequiredService<CompareExplorationsJobProcessor>());

        // Options binding lives in Program.cs alongside the other modules'
        // AddOptions<> chains, following the established layering rule:
        // modules expose Add*Handlers() for service registrations; the
        // options-pattern wiring is a Program.cs concern because it spans
        // configuration sources the module can't reach.
        return services;
    }
}
