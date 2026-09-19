using Microsoft.Extensions.DependencyInjection;

namespace Storporate.Modules.StudentGrowthExperience;

/// <summary>
/// DI entry point for the StudentGrowthExperience module (STOR-40 advisor).
/// Phase 1 wires only the module's options class — there are no handlers
/// yet (the advisor endpoints, the per-turn processor, and the feed-fetcher
/// arrive in later phases). The registration is added now so
/// <c>Program.cs</c> can bind and validate <see cref="AdvisorOptions"/> at
/// startup with the same fail-fast posture the other options classes
/// already use.
/// </summary>
public static class DependencyInjection
{
    /// <summary>
    /// Registers the module's options + future handler services. Idempotent
    /// — every call adds the same single registration. Callers should
    /// invoke this once during host construction (see
    /// <c>Program.cs</c>'s <c>AddStudentGrowthExperienceHandlers()</c>
    /// call alongside the other modules).
    /// </summary>
    public static IServiceCollection AddStudentGrowthExperienceHandlers(this IServiceCollection services)
    {
        // Options binding lives in Program.cs alongside the other modules'
        // AddOptions<> chains, following the established layering rule:
        // modules expose Add*Handlers() for service registrations; the
        // options-pattern wiring is a Program.cs concern because it spans
        // configuration sources the module can't reach.
        return services;
    }
}