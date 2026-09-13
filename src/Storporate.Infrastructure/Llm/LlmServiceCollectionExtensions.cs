using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Storporate.SharedKernel.Abstractions;

namespace Storporate.Infrastructure.Llm;

/// <summary>DI entry point wiring the Bionic-backed <see cref="ILlmClient"/> implementation.</summary>
public static class LlmServiceCollectionExtensions
{
    /// <summary>
    /// Registers a named <see cref="HttpClient"/> pointed at <see cref="BionicOptions.BaseUrl"/>
    /// and <see cref="ILlmClient"/> as <see cref="BionicLlmProvider"/>. Assumes
    /// <see cref="BionicOptions"/> is already bound and validated (see the Options-pattern
    /// registration in <c>Program.cs</c>).
    /// </summary>
    public static IServiceCollection AddBionicLlmProvider(this IServiceCollection services)
    {
        services.AddHttpClient(LlmHttpClientNames.Bionic, (serviceProvider, httpClient) =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<BionicOptions>>().Value;

            // BaseAddress must end in "/" for a relative request URI ("chat/completions") to
            // combine correctly instead of replacing the "/v1" path segment.
            httpClient.BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/");

            // Generous timeout: a local reasoning model can legitimately take a while to finish
            // its internal chain-of-thought before returning, especially on first load.
            httpClient.Timeout = TimeSpan.FromSeconds(120);
        });

        services.AddTransient<ILlmClient, BionicLlmProvider>();

        return services;
    }
}
