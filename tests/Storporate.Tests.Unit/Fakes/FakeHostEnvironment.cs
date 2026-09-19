using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Storporate.Tests.Unit.Fakes;

/// <summary>
/// Test double for <see cref="IHostEnvironment"/>. Default factory is
/// <see cref="Production"/> so a test that needs <see cref="IHostEnvironment.IsDevelopment"/>
/// to be <c>true</c> constructs <see cref="Development"/> explicitly instead of having to
/// remember to flip the default — the conservative choice, matching the unit suite's
/// "exercise the production-equivalent path" convention.
/// </summary>
/// <remarks>
/// Hand-written (no mocking framework) to match this suite's
/// (<c>FakeAuditLogWriter</c>, <c>FakeJwtTokenService</c>, <c>FakeGoogleIdTokenValidator</c>)
/// style: small, deterministic, with named factories for the two environments we actually
/// gate on rather than a mutable property bag tests have to remember to configure.
/// </remarks>
public sealed class FakeHostEnvironment : IHostEnvironment
{
    /// <summary>"Development" — matches <c>Microsoft.Extensions.Hosting.Hosts.LocalDevelopmentEnvironmentName</c>.</summary>
    public const string DevelopmentEnvironmentName = "Development";

    /// <summary>"Production" — the canonical ASP.NET Core non-Development environment name.</summary>
    public const string ProductionEnvironmentName = "Production";

    public FakeHostEnvironment(string environmentName)
    {
        EnvironmentName = environmentName;
    }

    /// <summary>Factory for the "Development environment" test case — enables IsDevelopment()-gated paths.</summary>
    public static FakeHostEnvironment Development() => new(DevelopmentEnvironmentName);

    /// <summary>Factory for the "Production environment" test case — disables every IsDevelopment()-gated path.</summary>
    public static FakeHostEnvironment Production() => new(ProductionEnvironmentName);

    public string EnvironmentName { get; set; }
    public string ApplicationName { get; set; } = "Storporate.Tests";
    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
