using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Persistence.Interceptors;

namespace Storporate.Tests.Unit.Auth;

/// <summary>
/// Lightweight <see cref="WebApplicationFactory{TEntryPoint}"/> for the Phase 3 [Authorize]
/// integration tests. Configures every Options-bound section with valid placeholder values so
/// the host's fail-fast <c>ValidateOnStart</c> pipeline doesn't reject startup, swaps the
/// <see cref="WriteDbContext"/> registration for an in-memory EF provider, and disables the
/// real Resend / LLM / S3-clients by overriding their config so they're never instantiated.
/// </summary>
public sealed class AuthEndpointsFactory : WebApplicationFactory<Program>
{
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.ConfigureHostConfiguration(configurationBuilder =>
        {
            configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // --- Options pattern placeholders (all required at startup) ---
                ["ConnectionStrings:WriteDb"] = "Host=localhost;Port=5432;Database=test;Username=u;Password=p",
                ["ConnectionStrings:SslMode"] = "Disable",
                ["ArtifactStorage:Provider"] = "MinIO",
                ["ArtifactStorage:Endpoint"] = "http://localhost:9000",
                ["ArtifactStorage:AccessKey"] = "test",
                ["ArtifactStorage:SecretKey"] = "test",
                ["ArtifactStorage:BucketName"] = "test",
                ["Llm:Bionic:BaseUrl"] = "http://localhost:1234/v1",
                ["Llm:Bionic:ModelId"] = "test-model",
                ["Encryption:FieldEncryptionKey"] = "CC40PEtQJ3kOFRSJA0Hz40p4pgahPMvxLdvoFIDPyno=",
                ["Resend:ApiKey"] = "re_test_placeholder",
                ["Resend:FromEmail"] = "test@example.com",
                ["Jwt:SigningKey"] = "yyl1HDUbAGIf15wifGiNzsASBlF0YH+QiwPSxwHEp0Nl9bLoT3ZXtdMBuZzR6AEe",
                ["Jwt:Issuer"] = "storporate-api",
                ["Jwt:Audience"] = "storporate-clients",
                ["Otp:CodeLength"] = "6",
                ["Otp:ExpiryMinutes"] = "10",
                ["Otp:MaxAttempts"] = "5",
                ["GoogleAuth:ClientId"] = "test-google-client-id",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Replace the Postgres-backed WriteDbContext with an in-memory EF provider so the
            // integration tests don't need a live Postgres. Done by removing all
            // DbContextOptions<WriteDbContext> registrations and adding a fresh in-memory one.
            var descriptors = services
                .Where(d => d.ServiceType.FullName?.Contains("DbContextOptions") == true
                    || d.ServiceType == typeof(WriteDbContext))
                .ToList();
            foreach (var descriptor in descriptors)
            {
                services.Remove(descriptor);
            }

            // STOR-62 Phase 4: WriteDbContext now requires IAccountContext. The factory
            // overload that resolves from the request scope wires the test-only
            // AmbientAccountContext (registered below) into every WriteDbContext the host
            // builds. The interceptor itself is intentionally NOT registered here — the
            // integration tests below exercise the JWT/permission/MVC pipeline, not the
            // tenancy guard, and skipping the interceptor means the test can save data
            // without going through UseAccountContext. Global query filter still applies
            // (it's installed at OnModelCreating time), so any IAccountScoped query these
            // tests might add would still be filtered — which is the right shape for an
            // integration test of unrelated code paths.
            services.AddDbContext<WriteDbContext>((sp, options) =>
                options.UseInMemoryDatabase("AuthEndpointsFactory")
                    .AddInterceptors(sp.GetRequiredService<RowLevelSecurityInterceptor>()));
            services.AddScoped<RowLevelSecurityInterceptor>();
        });

        builder.UseEnvironment(Environments.Development);

        return base.CreateHost(builder);
    }
}
