using System.Text;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Storporate.Api.Authorization;
using Storporate.Api.Configuration;
using Storporate.Api.Errors;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Email;
using Storporate.Infrastructure.Llm;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Persistence.Interceptors;
using Storporate.Infrastructure.Security;
using Storporate.Infrastructure.Security.RateLimiting;
using Storporate.Infrastructure.Storage;
using Storporate.Modules.Identity;
using Storporate.Modules.PlatformFoundations;
using Storporate.Modules.SecurityGovernance;
using Storporate.Modules.Portfolio;
using Storporate.Modules.PlatformFoundations.Diagnostics;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Security;
using Storporate.SharedKernel.Storage;

DotEnvLoader.LoadIfPresent();

var builder = WebApplication.CreateBuilder(args);

// --- CORS: allow the local Next.js dev server to call this API from the browser.
// Development-only policy; a production origin allowlist is a later story's concern. ---
const string FrontendDevCorsPolicy = "FrontendDevCorsPolicy";
builder.Services.AddCors(options =>
{
    options.AddPolicy(FrontendDevCorsPolicy, policy =>
        policy.WithOrigins("http://localhost:3000")
            .AllowAnyMethod()
            .AllowAnyHeader());
});

// --- Kestrel + FormOptions: STOR-37 sets the upload envelope to 100 MB to match
// CreatePortfolioItemValidator.MaxFileSizeBytes. Kestrel's MaxRequestBodySize AND
// FormOptions.MultipartBodyLengthLimit must both be raised — see the
// minimal-api-file-upload skill's "Two size limits" rule: configuring only one
// leaves uploads failing with the other cap before the body is ever parsed. ---
const long MaxPortfolioUploadBytes = 100L * 1024L * 1024L;
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = MaxPortfolioUploadBytes;
});
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = MaxPortfolioUploadBytes;
    options.ValueLengthLimit = (int)MaxPortfolioUploadBytes;
    options.MultipartHeadersLengthLimit = 16 * 1024;
});

// --- Options pattern: fail fast on missing/invalid configuration at startup, not later ---
builder.Services
    .AddOptions<ConnectionStringsOptions>()
    .Bind(builder.Configuration.GetSection(ConnectionStringsOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<ArtifactStorageOptions>()
    .Bind(builder.Configuration.GetSection(ArtifactStorageOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<BionicOptions>()
    .Bind(builder.Configuration.GetSection(BionicOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<FieldEncryptionOptions>()
    .Bind(builder.Configuration.GetSection(FieldEncryptionOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        options => AesGcmFieldEncryptor.IsValidKey(options.FieldEncryptionKey),
        $"{FieldEncryptionOptions.SectionName}:{nameof(FieldEncryptionOptions.FieldEncryptionKey)} must be a base64-encoded 32-byte (AES-256) key.")
    .ValidateOnStart();

builder.Services
    .AddOptions<ResendOptions>()
    .Bind(builder.Configuration.GetSection(ResendOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// STOR-64 Phase 1: SmtpOptions is bound unconditionally alongside ResendOptions — both
// providers' sections are validated at startup regardless of which is the active provider, by
// deliberate simplicity (both already have real values in .env today, so the cost is zero).
builder.Services
    .AddOptions<SmtpOptions>()
    .Bind(builder.Configuration.GetSection(SmtpOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<JwtOptions>()
    .Bind(builder.Configuration.GetSection(JwtOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        options => JwtOptions.IsValidSigningKey(options.SigningKey),
        $"{JwtOptions.SectionName}:{nameof(JwtOptions.SigningKey)} must be at least {JwtOptions.MinimumSigningKeyLengthBytes} bytes (UTF-8) for HMAC-SHA256 signing.")
    .ValidateOnStart();

builder.Services
    .AddOptions<OtpOptions>()
    .Bind(builder.Configuration.GetSection(OtpOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<OtpRateLimitOptions>()
    .Bind(builder.Configuration.GetSection(OtpRateLimitOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<GoogleAuthOptions>()
    .Bind(builder.Configuration.GetSection(GoogleAuthOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

// --- Persistence ---
// STOR-62 Phase 4: register the RowLevelSecurityInterceptor in DI so EF Core's
// AddDbContext<WriteDbContext> factory (below) can resolve it from the request scope
// alongside the DbContext itself. The interceptor depends on the singleton IAccountContext
// (registered in AddAuthorizationPolicies) — that lifetime pairing is safe because
// AddAuthorizationPolicies registers AmbientAccountContext as a singleton with AsyncLocal
// storage, so a singleton holding a reference to a singleton is exactly what we want, not
// a captive-dependency bug.
builder.Services.AddScoped<RowLevelSecurityInterceptor>();

builder.Services.AddDbContext<WriteDbContext>((serviceProvider, options) =>
{
    var connectionStrings = serviceProvider.GetRequiredService<IOptions<ConnectionStringsOptions>>().Value;
    options.UseNpgsql(connectionStrings.ToNpgsqlConnectionString());

    // AddInterceptors<T>() resolves the interceptor from the DbContext's service provider
    // on every DbContext construction, which matches the interceptor's scoped registration
    // above. The same WriteDbContext instance carries the same interceptor instance, so
    // SavingChanges / ConnectionOpened callbacks stay coherent across a single request.
    options.AddInterceptors(serviceProvider.GetRequiredService<RowLevelSecurityInterceptor>());
});

// --- Modules ---
builder.Services.AddPlatformFoundationsHandlers();
builder.Services.AddIdentityHandlers();
builder.Services.AddSecurityGovernanceHandlers();
builder.Services.AddPortfolioHandlers();

// --- Email provider switch (STOR-64 Phase 1): the first real DI-branching switch in this
// codebase. Selected via `Email:Provider` ("Resend" or "Smtp"). Lives here, alongside every
// other cross-cutting config/DI decision, rather than inside AddIdentityHandlers() — so the
// Identity module stays unaware of which concrete sender is wired up (it only knows about
// IEmailSender) and there's exactly one place in the codebase that picks the provider.
// `AddIdentityHandlers()` no longer calls `AddResendEmailSender()` directly for that reason. ---
var emailProvider = builder.Configuration.GetValue<string>("Email:Provider") ?? "Resend";
switch (emailProvider)
{
    case "Smtp":
        builder.Services.AddSmtpEmailSender();
        break;
    case "Resend":
        builder.Services.AddResendEmailSender();
        break;
    default:
        throw new InvalidOperationException(
            $"Email:Provider must be 'Resend' or 'Smtp' (got '{emailProvider}').");
}

// --- AI provider (Bionic-hosted local LLM, OpenAI-compatible) ---
builder.Services.AddBionicLlmProvider();

// --- Artifact storage (S3-compatible; local MinIO now, real Cloudflare R2 later) ---
builder.Services.AddArtifactStorage();

// --- JWT access/refresh token issuance (STOR-61 Phase 2) ---
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();

// --- Google ID token validation (STOR-61 Phase 3) ---
builder.Services.AddScoped<IGoogleIdTokenValidator, GoogleIdTokenValidator>();

// --- JWT bearer authentication for [Authorize] endpoints (STOR-61 Phase 3) ---
// Reuses JwtOptions (same key/issuer/audience as the issuer, mirrored exactly) so an access
// token minted by /api/auth/otp/verify, /api/auth/google, or /api/auth/refresh is accepted by
// /me, /sessions, /logout, and /logout-all with no extra configuration.
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        var jwtOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
            ?? new JwtOptions();

        // JwtOptions has already been validated by the AddOptions().ValidateOnStart() chain
        // above; bail early here only on the impossible-config case so the host never starts.
        if (!JwtOptions.IsValidSigningKey(jwtOptions.SigningKey))
        {
            throw new InvalidOperationException(
                $"{JwtOptions.SectionName}:{nameof(JwtOptions.SigningKey)} must be at least {JwtOptions.MinimumSigningKeyLengthBytes} bytes for HMAC-SHA256 signing.");
        }

        // Without this, ASP.NET Core's JwtSecurityTokenHandler applies its legacy
        // DefaultInboundClaimTypeMap when validating an incoming token, silently renaming
        // standard claims on the ClaimsPrincipal it builds (e.g. "sub" -> ClaimTypes.NameIdentifier,
        // "email" -> ClaimTypes.Email). JwtTokenService issues tokens using the raw
        // JwtRegisteredClaimNames values, and every [Authorize]-gated handler (GetCurrentUserHandler,
        // LogoutHandler, LogoutAllHandler, ListSessionsHandler) reads claims back via those same raw
        // JwtRegisteredClaimNames constants, so the remapped types would never be found. Disabling
        // the remap keeps the claim types issued == the claim types read.
        options.MapInboundClaims = false;

        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddAuthorizationPolicies();

// --- OTP rate limiting: chained per-email fixed-window + per-IP token-bucket (STOR-61 Phase 2).
// Read directly off configuration (rather than via the DI-resolved, validated IOptions<T>) purely
// because RateLimiterOptions' own configure delegate has no service-provider-aware overload to
// bind from; OtpRateLimitOptions is still separately registered above through the standard
// Options pattern so ValidateOnStart's fail-fast behavior is exercised regardless. ---
var otpRateLimitOptions = builder.Configuration.GetSection(OtpRateLimitOptions.SectionName).Get<OtpRateLimitOptions>()
    ?? new OtpRateLimitOptions();

builder.Services.AddRateLimiter(options =>
{
    options.AddOtpCombinedPolicy(otpRateLimitOptions);

    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
        }

        await context.HttpContext.Response.WriteAsJsonAsync(
            new ErrorResponse("otp_rate_limit_exceeded", "Too many requests. Please try again later."),
            cancellationToken);
    };
});

// --- FluentValidation: one validator per use case, discovered by scanning the Modules assembly ---
// (fully qualified: both Storporate.Modules.PlatformFoundations and Storporate.Modules.Identity
// declare a "DependencyInjection" type, so an unqualified reference would be ambiguous now that
// both namespaces are imported above — either type's assembly is the same Modules assembly.)
builder.Services.AddValidatorsFromAssembly(typeof(Storporate.Modules.PlatformFoundations.DependencyInjection).Assembly);

// --- Global error handling ---
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();

builder.Services.AddOpenApi();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// --- HSTS: tell browsers to remember HTTPS-only for this host. Skipped in Development so the
// dev cert / plain-http loop never gets a lingering browser HSTS entry for localhost. ---
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseCors(FrontendDevCorsPolicy);

// --- OTP rate limiting: the email-capture middleware must run before UseRateLimiter() so the
// per-email partition-key function (which runs inside UseRateLimiter) can see the parsed body. ---
app.UseOtpRateLimitEmailCapture();
app.UseRateLimiter();

// --- Authentication / authorization for [Authorize] endpoints (STOR-61 Phase 3). Must run
// AFTER UseCors (so preflight CORS requests aren't asked for credentials they don't have) and
// AFTER UseRateLimiter (so brute-force /me probing is rate-limited the same as /otp/*), and
// BEFORE endpoint mapping. ---
app.UseAuthentication();

// --- STOR-62 Phase 3: ambient account context. Sits between UseAuthentication() (so the JWT
// sub/actor_type claims are already on HttpContext.User) and UseAuthorization() (so every
// RequirePermission gate resolves against the populated IAccountContext). The route-value
// lookup for {accountId} also needs routing to have run, which UseAuthentication above
// implicitly triggers in modern WebApplication pipelines. ---
app.UseAccountContext();

app.UseAuthorization();

app.MapIdentityEndpoints();
app.MapAuditLogEndpoints();
app.MapPortfolioEndpoints();

// --- Temporary diagnostics endpoints (Phase 2: validation/exception-handler proof; Phase 3/4
// add llm-ping/storage-ping alongside these) ---
app.MapPost("/api/diagnostics/echo", (EchoRequest request, IValidator<EchoRequest> validator) =>
{
    validator.ValidateAndThrow(request);
    return Results.Ok(new { echoed = request.Message });
});

app.MapGet("/api/diagnostics/boom", () =>
{
    throw new InvalidOperationException("Deliberate diagnostic failure for exception-handler verification.");
});

app.MapGet("/api/diagnostics/llm-ping", async (ILlmClient llmClient, CancellationToken cancellationToken) =>
{
    var result = await llmClient.CompleteAsync(
        new LlmCompletionRequest(UserPrompt: "Say OK.", MaxOutputTokens: 200),
        cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/api/diagnostics/storage-ping", async (IArtifactStore artifactStore, CancellationToken cancellationToken) =>
{
    var result = await StoragePingHandler.ExecuteAsync(artifactStore, cancellationToken);
    return Results.Ok(result);
});

app.Run();

// Required for WebApplicationFactory-based integration testing later.
public partial class Program;
