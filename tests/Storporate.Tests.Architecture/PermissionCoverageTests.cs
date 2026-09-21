using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Storporate.Api.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Authorization;

namespace Storporate.Tests.Architecture;

/// <summary>
/// STOR-62 Phase 6: build-failing coverage gate ensuring every mapped endpoint either
/// declares the permission it requires via <see cref="RequirePermissionAttribute"/> or has
/// an explicit, commented entry in <see cref="UnauthenticatedEndpoints"/> below. New
/// endpoints added without a declaration fail the build, naming the exact offending
/// route + method.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an allow-list, not a blanket "RequirePermission or fail."</b> The
/// authentication flows (<c>/api/auth/otp/request</c>, <c>/otp/verify</c>,
/// <c>/google</c>, <c>/refresh</c>) run before any account exists, so there is no
/// permission to enforce against; matching Docomate's own allow-list reasoning for the
/// equivalent endpoints. The self-scoped session flows (<c>/logout</c>,
/// <c>/logout-all</c>, <c>/me</c>, <c>/sessions</c>) already require authentication
/// via <c>.RequireAuthorization()</c> and act only on the caller's own session, never
/// on another account's data, so no cross-account <c>RequirePermission</c> applies
/// yet. Every entry below is paired with a comment explaining the rationale — if the
/// rationale no longer applies (e.g. <c>/me</c> starts surfacing another account's
/// profile), the comment becomes the prompt to add a <c>RequirePermission</c> call
/// instead of leaving the entry behind.
/// </para>
/// <para>
/// <b>Why enumerate <see cref="EndpointDataSource"/> rather than reflect over
/// controllers.</b> storporate.backend uses minimal APIs exclusively — there are no
/// <c>[ApiController]</c> types anywhere. <see cref="EndpointDataSource"/> is the
/// framework-native enumeration of every route ASP.NET Core has built, and it is
/// what real HTTP requests route through; reflecting over <c>ControllerBase</c>
/// subtypes (Docomate's approach) would silently miss every endpoint in this
/// codebase.
/// </para>
/// <para>
/// <b>Why this lives in <c>Tests.Architecture</c>, not <c>Tests.Unit</c>.</b> It is a
/// build-failing structural gate on the endpoint surface, not a unit test of any one
/// class. Per the STOR-62 plan it is filed under architecture tests.
/// </para>
/// </remarks>
public class PermissionCoverageTests
{
    /// <summary>
    /// Endpoints that do <i>not</i> require a <see cref="RequirePermissionAttribute"/> and
    /// are explicitly opted out, with a one-line justification. Every entry must carry a
    /// comment; the format of the tuple value is <c>(HTTP method, route pattern)</c> as
    /// surfaced by <see cref="RouteEndpoint.RoutePattern.RawText"/> combined with
    /// <see cref="HttpMethod"/> parsed off the endpoint metadata.
    /// </summary>
    /// <remarks>
    /// Two categories of entry today:
    /// <list type="bullet">
    ///   <item><b>Authentication bootstrap</b> — flows that mint or refresh the caller's
    ///   identity, where no ambient account exists yet at call time, so no
    ///   <c>RequirePermission</c> could resolve. Docomate's own coverage test allow-lists
    ///   the equivalent routes for the same reason.</item>
    ///   <item><b>Self-scoped session operations</b> — flows that already require
    ///   authentication (<c>.RequireAuthorization()</c>) and act only on the caller's own
    ///   session data; they don't touch another account, so the cross-account permission
    ///   model has nothing to say.</item>
    /// </list>
    /// </remarks>
    private static readonly HashSet<(string Method, string Pattern)> UnauthenticatedEndpoints = new()
    {
        // --- Identity module (src/Storporate.Modules/Identity/IdentityEndpoints.cs) ---

        // POST /api/auth/otp/request — anonymous; runs before any account exists, so
        // no permission could resolve against an IAccountContext.
        ("POST", "/api/auth/otp/request"),

        // POST /api/auth/otp/verify — anonymous; mints the JWT that establishes the
        // caller's identity and is the precondition for every other identity endpoint.
        ("POST", "/api/auth/otp/verify"),

        // POST /api/auth/google — anonymous; Google-ID-token-based login equivalent of
        // /otp/verify for users with a verified Google identity.
        ("POST", "/api/auth/google"),

        // POST /api/auth/refresh — anonymous; exchanges a refresh token for a new access
        // token before the bearer middleware has re-established the principal.
        ("POST", "/api/auth/refresh"),

        // POST /api/auth/logout — self-scoped; .RequireAuthorization() already gates this
        // and the handler revokes the caller's own session only, never another account's.
        ("POST", "/api/auth/logout"),

        // POST /api/auth/logout-all — self-scoped; same reasoning as /logout; revokes
        // every session belonging to the caller.
        ("POST", "/api/auth/logout-all"),

        // GET /api/auth/me — self-scoped; returns the caller's own profile, never another
        // account's.
        ("GET", "/api/auth/me"),

        // GET /api/auth/sessions — self-scoped; lists the caller's own active sessions.
        ("GET", "/api/auth/sessions"),

        // --- Temporary diagnostics endpoints (src/Storporate.Api/Program.cs) ---
        // Not in the STOR-62 Phase 6 plan's explicit scope (which named only
        // IdentityEndpoints.cs), but they are real, mapped ASP.NET Core routes on the
        // production endpoint surface. The plan's coverage test enumerates every
        // RouteEndpoint — omitting these would let them silently slip past the gate the
        // next time someone adds a diagnostics handler that does touch account data.
        // They have no account concept (smoke probes for validation, exception handling,
        // the LLM provider, and the artifact store); when they're replaced or removed,
        // the entries below go with them.

        // POST /api/diagnostics/echo — anonymous validation-ping; never touches a DB row.
        ("POST", "/api/diagnostics/echo"),

        // GET /api/diagnostics/boom — anonymous exception-handler verification probe;
        // deliberately throws, never touches a DB row.
        ("GET", "/api/diagnostics/boom"),

        // GET /api/diagnostics/llm-ping — anonymous LLM-provider smoke probe; never
        // touches a DB row.
        ("GET", "/api/diagnostics/llm-ping"),

        // POST /api/diagnostics/storage-ping — anonymous artifact-storage smoke probe;
        // never touches a DB row.
        ("POST", "/api/diagnostics/storage-ping"),

        // --- Framework-managed endpoints ---

        // GET /openapi/{documentName}.json — the OpenAPI document endpoint added by
        // `app.MapOpenApi()` in Program.cs. It is metadata about the API surface, not a
        // handler that reads or writes account-owned data; gating it with a permission
        // policy would only make the OpenAPI tooling less useful without any security
        // gain (the underlying handlers it documents are still permission-gated).
        ("GET", "/openapi/{documentName}.json"),
    };

    [Fact]
    public void EveryEndpoint_DeclaresRequiredPermission_OrIsAllowListed()
    {
        using var factory = new PermissionCoverageFactory();
        var endpointDataSource = factory.Services.GetRequiredService<EndpointDataSource>();

        var violations = CollectViolations(endpointDataSource.Endpoints);

        Assert.True(
            violations.Count == 0,
            "The following endpoints are mapped without a [RequirePermission] "
            + "(or other declaration) and are not in the PermissionCoverageTests "
            + "allow-list. Add `.RequirePermission(...)` to the endpoint, or add a "
            + "justifying entry to `UnauthenticatedEndpoints`:\n"
            + string.Join("\n", violations.Select(v => $"  - {v.Method} {v.Pattern}")));
    }

    /// <summary>
    /// Regression guard for the coverage gate itself. Builds a small standalone
    /// host with one deliberately un-annotated endpoint and asserts the gate flags
    /// it. Kept as a permanent test (rather than a one-off manual check) so a future
    /// refactor of <see cref="CollectViolations"/> cannot silently neuter the gate.
    /// </summary>
    /// <remarks>
    /// Uses an isolated host rather than the production <c>WebApplicationFactory&lt;Program&gt;</c>
    /// because the regression scenario only needs <see cref="EndpointDataSource"/>
    /// populated; spinning up the full DI graph (JWT, EF Core interceptor, Options
    /// validation, etc.) just to host one throwaway route would conflate two concerns.
    /// </remarks>
    [Fact]
    public void CoverageGate_FlagsUnannotatedEndpoints_ThatAreNotAllowListed()
    {
        using var host = BuildIsolatedHost(b => b.MapPost("/__test_unannotated_endpoint", () => Results.Ok()));
        var endpointDataSource = host.Services.GetRequiredService<EndpointDataSource>();

        var violations = CollectViolations(endpointDataSource.Endpoints)
            .Where(v => v.Method == "POST" && v.Pattern == "/__test_unannotated_endpoint")
            .ToList();

        Assert.True(
            violations.Count == 1,
            "Expected the coverage gate to flag the unannotated throwaway endpoint "
            + "POST /__test_unannotated_endpoint, but it did not. Either the gate is "
            + "broken (no longer detects unannotated endpoints) or the test scaffolding "
            + "is no longer wiring the throwaway endpoint through the host.");
    }

    /// <summary>
    /// Walks the endpoint data source and returns one record per endpoint that lacks
    /// a <see cref="RequirePermissionAttribute"/> in its metadata and whose
    /// <c>(HTTP method, route pattern)</c> is not in <see cref="UnauthenticatedEndpoints"/>.
    /// </summary>
    /// <remarks>
    /// Public-internal so the regression test can reuse the exact same check; if the
    /// logic ever drifts between the two callers, the regression test would catch it.
    /// </remarks>
    internal static List<(string Method, string Pattern)> CollectViolations(IEnumerable<Endpoint> endpoints)
    {
        var violations = new List<(string Method, string Pattern)>();

        foreach (var endpoint in endpoints)
        {
            if (endpoint is not RouteEndpoint routeEndpoint)
            {
                continue;
            }

            var pattern = routeEndpoint.RoutePattern.RawText;
            if (pattern is null)
            {
                // A RouteEndpoint with a null RawText is malformed; nothing meaningful to
                // report, and the framework would have rejected it at runtime anyway.
                continue;
            }

            var method = ExtractHttpMethod(endpoint);
            if (method is null)
            {
                // Non-HTTP endpoints (e.g. SignalR hubs) don't carry HttpMethodMetadata;
                // the gate is about the HTTP API surface, so they're out of scope.
                continue;
            }

            if (endpoint.Metadata.GetMetadata<RequirePermissionAttribute>() is not null)
            {
                continue;
            }

            if (UnauthenticatedEndpoints.Contains((method, pattern)))
            {
                continue;
            }

            violations.Add((method, pattern));
        }

        return violations;
    }

    /// <summary>
    /// Pulls the HTTP method off an endpoint's metadata. ASP.NET Core records it via
    /// <see cref="HttpMethodMetadata"/> on every endpoint produced by
    /// <c>MapGet</c>/<c>MapPost</c>/etc. Endpoints with no <see cref="HttpMethodMetadata"/>
    /// are not HTTP-mapped routes (e.g. SignalR hubs); the caller treats those as
    /// out-of-scope.
    /// </summary>
    private static string? ExtractHttpMethod(Endpoint endpoint)
    {
        var httpMethodMetadata = endpoint.Metadata.GetMetadata<HttpMethodMetadata>();
        if (httpMethodMetadata is null)
        {
            return null;
        }

        // HttpMethodMetadata.HttpMethods may contain "*" for any-method endpoints, or
        // multiple methods for one route (less common in minimal APIs but possible).
        // The first concrete one is reported in the failure message; "*" surfaces
        // verbatim — there shouldn't be any in this codebase and the failure message
        // would make the unexpected case obvious.
        var firstMethod = httpMethodMetadata.HttpMethods.FirstOrDefault();
        return string.IsNullOrEmpty(firstMethod) ? null : firstMethod;
    }

    /// <summary>
    /// <see cref="WebApplicationFactory{TEntryPoint}"/> tuned for the permission-coverage
    /// gate. Mirrors <c>AuthEndpointsFactory</c>'s Options-placeholder pattern so the
    /// host's <c>ValidateOnStart</c> chain doesn't reject startup, and swaps the
    /// production <see cref="WriteDbContext"/> for an in-memory EF provider so this
    /// test never needs a live Postgres.
    /// </summary>
    private sealed class PermissionCoverageFactory : WebApplicationFactory<Program>
    {
        protected override IHost CreateHost(IHostBuilder builder)
        {
            builder.ConfigureHostConfiguration(configurationBuilder =>
            {
                configurationBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Options pattern placeholders — all required at startup.
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
                    // STOR-64 Phase 1: SmtpOptions is bound and validated at startup
                    // (see Program.cs). The placeholder values are validated-only; no SMTP
                    // traffic is actually generated by this factory. Without these the
                    // host refuses to start after STOR-64 introduced the SMTP provider.
                    ["Email:Provider"] = "Smtp",
                    ["Smtp:Host"] = "smtp.test.invalid",
                    ["Smtp:Port"] = "587",
                    ["Smtp:Username"] = "test-smtp-user",
                    ["Smtp:Password"] = "test-smtp-password",
                    ["Smtp:FromEmail"] = "test@example.com",
                });
            });

            builder.ConfigureServices(services =>
            {
                // Replace the Postgres-backed WriteDbContext with an in-memory provider so
                // the host can fully start (the global query filter + interceptor wiring
                // runs at startup, not just at first request). The interceptor's Npgsql
                // GUC writer is a no-op under InMemory, so no connection is ever opened.
                // The FullName-substring filter catches the IDbContextOptionsConfiguration<T>
                // internal registrations too — without removing those, the Npgsql-bound
                // configuration leaks past the new AddDbContext call. See
                // AuthEndpointsFactory for the same pattern.
                var descriptors = services
                    .Where(d => d.ServiceType.FullName?.Contains("DbContextOptions") == true
                        || d.ServiceType == typeof(WriteDbContext))
                    .ToList();
                foreach (var descriptor in descriptors)
                {
                    services.Remove(descriptor);
                }

                services.AddDbContext<WriteDbContext>((sp, options) =>
                    options.UseInMemoryDatabase("PermissionCoverageFactory"));
            });

            builder.UseEnvironment(Environments.Development);

            return base.CreateHost(builder);
        }
    }

    /// <summary>
    /// Builds a minimal API host with a caller-supplied endpoint-mapping lambda, then
    /// returns the started <see cref="IHost"/>. Used by the regression test to assert
    /// the gate catches a deliberately-unannotated route; deliberately light-weight so
    /// the regression test isn't tied to the production app's full DI surface.
    /// </summary>
    private static IHost BuildIsolatedHost(Action<IEndpointRouteBuilder> mapEndpoints)
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();
        mapEndpoints(app);
        app.Start();
        return app;
    }
}
