using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Security;

namespace Storporate.Tests.Unit.Security;

public class OtpRateLimitOptionsTests
{
    [Fact]
    public void Value_WithNegativePerEmailWindow_ThrowsOptionsValidationException()
    {
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["OtpRateLimit:PerEmailWindowMinutes"] = "-5",
        });

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<OtpRateLimitOptions>>().Value);
    }

    [Fact]
    public void Value_WithZeroPerIpTokenLimit_ThrowsOptionsValidationException()
    {
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["OtpRateLimit:PerIpTokenLimit"] = "0",
        });

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<OtpRateLimitOptions>>().Value);
    }

    [Fact]
    public void Value_WithNoConfiguration_UsesDocumentedDefaults()
    {
        var provider = BuildProvider([]);

        var options = provider.GetRequiredService<IOptions<OtpRateLimitOptions>>().Value;

        Assert.Equal(10, options.PerEmailWindowMinutes);
        Assert.Equal(3, options.PerEmailMaxRequests);
        Assert.Equal(10, options.PerIpWindowMinutes);
        Assert.Equal(20, options.PerIpTokenLimit);
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> configurationValues)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configurationValues).Build();

        var services = new ServiceCollection();
        services
            .AddOptions<OtpRateLimitOptions>()
            .Bind(configuration.GetSection(OtpRateLimitOptions.SectionName))
            .ValidateDataAnnotations();

        return services.BuildServiceProvider();
    }
}
