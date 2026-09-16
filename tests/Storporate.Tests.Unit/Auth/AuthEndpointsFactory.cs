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
using Storporate.SharedKernel.Storage;
using Storporate.Tests.Unit.Fakes;

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
                // STOR-64 Phase 1: SmtpOptions is bound unconditionally at startup (see Program.cs),
                // so the test host's ValidateOnStart needs placeholder values here too — regardless
                // of which provider Email:Provider actually selects. Default to Resend for tests
                // since that's what the production test factory was already wired for.
                ["Email:Provider"] = "Resend",
                ["Smtp:Host"] = "smtp.test.invalid",
                ["Smtp:Port"] = "587",
                ["Smtp:Username"] = "test-smtp-user",
                ["Smtp:Password"] = "test-smtp-password",
                ["Smtp:FromEmail"] = "test@example.com",
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
            // integration tests don't need a live Postgres. Done by removing every EF Core
            // DbContextOptions descriptor (both the open-generic and the closed
            // DbContextOptions<WriteDbContext> shape) and the WriteDbContext itself before
            // re-registering with the in-memory provider. The FullName-substring filter
            // catches the IDbContextOptionsConfiguration<T> internal registrations too,
            // which is what makes the swap coherent — without removing those, the Npgsql-
            // bound configuration leaks past the new AddDbContext call.
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
            // builds. The RowLevelSecurityInterceptor is registered so its
            // SavingChanges[Async] hook fires (it no-ops on InMemory — no Npgsql
            // connection type — for the GUC refresh path; it still enforces
            // AccountScope on the change tracker). Global query filter still applies
            // (it's installed at OnModelCreating time), so any IAccountScoped query
            // these tests might add would still be filtered — which is the right shape
            // for an integration test of unrelated code paths.
            services.AddDbContext<WriteDbContext>((sp, options) =>
                options.UseInMemoryDatabase("AuthEndpointsFactory")
                    .AddInterceptors(sp.GetRequiredService<RowLevelSecurityInterceptor>()));
            services.AddScoped<RowLevelSecurityInterceptor>();

            // Swap IArtifactStore for the in-memory fake so handler/endpoint tests can
            // assert which keys were written or removed without hitting MinIO/S3.
            // The MinIO/S3 client would otherwise try to construct itself against
            // unreachable localhost endpoints. No test in this fixture cares about the
            // real storage backend's behavior — they care about *whether* something was
            // written, which the fake records verbatim.
            services.RemoveAll<IArtifactStore>();
            services.AddSingleton<IArtifactStore, FakeArtifactStore>();
        });

        builder.UseEnvironment(Environments.Development);

        return base.CreateHost(builder);
    }
}
