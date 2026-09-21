using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Persistence.Interceptors;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Storage;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.DiscoveryHiring.Fakes;

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> for STOR-44 Phase 2
/// drill-down endpoint tests. Self-contained (does not inherit from
/// <see cref="Auth.AuthEndpointsFactory"/>) so the seed-only
/// <see cref="SeededArtifactStore"/> swap is guaranteed to win the
/// IArtifactStore resolution — no inheritance chain where a base
/// factory's <c>ConfigureServices</c> callback can stomp on the
/// subclass's <c>ConfigureTestServices</c> swap at the very last
/// Build step.
/// </summary>
/// <remarks>
/// <para>
/// All of <see cref="Auth.AuthEndpointsFactory"/>'s setup is duplicated
/// here verbatim — every Options placeholder, every in-memory EF swap,
/// every <see cref="IAuditLogWriter"/> / <see cref="IEmbeddingClient"/> /
/// <see cref="ILlmClient"/> fake registration. The duplication is
/// deliberate: Phase 2's per-key ContentType contract requires the
/// artifact store to be the <see cref="SeededArtifactStore"/> for the
/// lifetime of the host, and the cleanest way to guarantee that is to
/// own the whole factory. Phase 1's
/// <see cref="Auth.DiscoveryHiringEndpointsFactory"/> inherits from
/// the Auth one and uses the generic <see cref="FakeArtifactStore"/>
/// (always <c>application/octet-stream</c>), which is fine for Phase 1
/// because the talent-search surface never reads artifact content
/// types. Phase 2 reads them, so the swap has to be unconditional.
/// </para>
/// </remarks>
public sealed class CandidateReviewEndpointsFactory : WebApplicationFactory<Program>
{
    /// <summary>Unique-per-instance database name so multiple
    /// <see cref="CandidateReviewEndpointsFactory"/> instances — one per
    /// <see cref="IClassFixture{TFixture}"/> — don't share the
    /// InMemory store.</summary>
    private readonly string _databaseName = "CandidateReviewEndpointsFactory-" + Guid.NewGuid().ToString("N");

    /// <summary>The <see cref="SeededArtifactStore"/> the host resolves
    /// against <see cref="IArtifactStore"/>. Tests reach the same
    /// instance via <c>_factory.ArtifactStore</c> to register storage
    /// entries + assert the streaming handler read the right key.</summary>
    public SeededArtifactStore ArtifactStore { get; } = new();

    /// <summary>The <see cref="FakeAuditLogWriter"/> the host resolves
    /// against <see cref="IAuditLogWriter"/>. Tests reach the same
    /// instance via <c>_factory.AuditLogWriter</c> to inspect the
    /// <c>candidate_reviewed</c> + <c>candidate_original_opened</c>
    /// audit rows the drill-down handlers write.</summary>
    public FakeAuditLogWriter AuditLogWriter { get; } = new();

    public FakeEmbeddingClient EmbeddingClient { get; } = new();
    public FakeLlmClient LlmClient { get; } = new();

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
                ["Advisor:MaxUserPromptCharacters"] = "24000",
                ["Advisor:MaxPortfolioItemCharacters"] = "4000",
                ["Advisor:MaxFeedCandidateCharacters"] = "600",
                ["Advisor:MaxFeedCandidates"] = "12",
                ["Advisor:HistoryWindowTurns"] = "6",
                ["Advisor:MaxHistoryEntryCharacters"] = "3000",
                ["Advisor:MaxOutputTokens"] = "4096",
                ["Advisor:HttpTimeout"] = "00:02:00",
                ["Advisor:MaxExplorationsPerStudent"] = "20",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Replace the Postgres-backed WriteDbContext with an in-memory EF
            // provider so the integration tests don't need a live Postgres.
            var descriptors = services
                .Where(d => d.ServiceType.FullName?.Contains("DbContextOptions") == true
                    || d.ServiceType == typeof(WriteDbContext))
                .ToList();
            foreach (var descriptor in descriptors)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<WriteDbContext>((sp, options) =>
                options.UseInMemoryDatabase(_databaseName)
                    .AddInterceptors(sp.GetRequiredService<RowLevelSecurityInterceptor>()));
            services.AddScoped<RowLevelSecurityInterceptor>();

            // Per-key content-type-aware artifact store. Replaces the
            // production S3ArtifactStore so the Phase 2 "Content-Type:
            // stored type" wire contract is honored end-to-end.
            services.RemoveAll<IArtifactStore>();
            services.AddSingleton<IArtifactStore>(_ => ArtifactStore);

            // IAuditLogWriter is registered as Scoped by
            // SecurityGovernance/DependencyInjection.cs. Replace it
            // with the singleton fake so the endpoint tests can
            // inspect Recorded entries without going through the
            // (no-op under InMemory) production Postgres path. The
            // concrete FakeAuditLogWriter type is also registered so
            // the tests can resolve it directly via
            // _factory.Services.GetRequiredService<FakeAuditLogWriter>().
            services.RemoveAll<IAuditLogWriter>();
            services.AddSingleton<IAuditLogWriter>(_ => AuditLogWriter);
            services.RemoveAll<FakeAuditLogWriter>();
            services.AddSingleton(_ => AuditLogWriter);

            // IEmbeddingClient is registered as Transient by
            // LlmServiceCollectionExtensions.AddBionicEmbeddings. Remove
            // the production BionicEmbeddingClient and install the fake
            // so the refresh processor (if exercised) gets deterministic
            // embeddings without touching localhost:1234.
            services.RemoveAll<IEmbeddingClient>();
            services.AddSingleton(_ => EmbeddingClient);
            services.AddTransient<IEmbeddingClient>(_ => EmbeddingClient);

            // SearchTalentJobProcessor also depends on ILlmClient.
            services.RemoveAll<ILlmClient>();
            services.AddSingleton<ILlmClient>(_ => LlmClient);
        });

        builder.UseEnvironment(Environments.Development);

        return base.CreateHost(builder);
    }
}
