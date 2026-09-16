using Microsoft.Extensions.DependencyInjection;

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
public static class DependencyInjection
{
    public static IServiceCollection AddPortfolioHandlers(this IServiceCollection services)
    {
        return services;
    }
}