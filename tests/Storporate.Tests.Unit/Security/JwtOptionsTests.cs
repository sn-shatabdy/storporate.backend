using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Security;

namespace Storporate.Tests.Unit.Security;

/// <summary>
/// Exercises the exact Options-pattern registration used in Program.cs (Bind + ValidateDataAnnotations
/// + custom signing-key-length Validate), confirming it fails fast on invalid configuration.
/// </summary>
public class JwtOptionsTests
{
    [Fact]
    public void Value_WithSigningKeyShorterThanMinimumLength_ThrowsOptionsValidationException()
    {
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = "too-short",
            ["Jwt:Issuer"] = "storporate-api",
            ["Jwt:Audience"] = "storporate-clients",
        });

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<JwtOptions>>().Value);
    }

    [Fact]
    public void Value_WithMissingIssuer_ThrowsOptionsValidationException()
    {
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = new string('a', 32),
            ["Jwt:Audience"] = "storporate-clients",
        });

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<JwtOptions>>().Value);
    }

    [Fact]
    public void Value_WithValidConfiguration_BindsAndAppliesDocumentedDefaults()
    {
        var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = new string('a', 32),
            ["Jwt:Issuer"] = "storporate-api",
            ["Jwt:Audience"] = "storporate-clients",
        });

        var options = provider.GetRequiredService<IOptions<JwtOptions>>().Value;

        Assert.Equal(15, options.AccessTokenMinutes);
        Assert.Equal(7, options.RefreshTokenDays);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(1)]
    public void IsValidSigningKey_WithKeyShorterThanMinimum_ReturnsFalse(int length) =>
        Assert.False(JwtOptions.IsValidSigningKey(new string('a', length)));

    [Fact]
    public void IsValidSigningKey_WithMinimumLengthKey_ReturnsTrue() =>
        Assert.True(JwtOptions.IsValidSigningKey(new string('a', JwtOptions.MinimumSigningKeyLengthBytes)));

    private static ServiceProvider BuildProvider(Dictionary<string, string?> configurationValues)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configurationValues).Build();

        var services = new ServiceCollection();
        services
            .AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                options => JwtOptions.IsValidSigningKey(options.SigningKey),
                $"{JwtOptions.SectionName}:{nameof(JwtOptions.SigningKey)} must be at least {JwtOptions.MinimumSigningKeyLengthBytes} bytes.");

        return services.BuildServiceProvider();
    }
}
