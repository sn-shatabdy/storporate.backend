using System.Text;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Storporate.Api.Configuration;
using Storporate.Api.Errors;
using Storporate.Infrastructure.Email;
using Storporate.Infrastructure.Llm;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Security;
using Storporate.Infrastructure.Security.RateLimiting;
using Storporate.Infrastructure.Storage;
using Storporate.Modules.Identity;
using Storporate.Modules.PlatformFoundations;
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
builder.Services.AddDbContext<WriteDbContext>((serviceProvider, options) =>
{
    var connectionStrings = serviceProvider.GetRequiredService<IOptions<ConnectionStringsOptions>>().Value;

    // Layer the configured SSL mode onto the connection string rather than baking it into
    // ConnectionStrings__WriteDb directly, so local dev's plaintext Docker Postgres and a future
    // hosted Postgres that requires TLS can share the same base connection string shape.
    var connectionStringBuilder = new NpgsqlConnectionStringBuilder(connectionStrings.WriteDb)
    {
        SslMode = Enum.Parse<SslMode>(connectionStrings.SslMode, ignoreCase: true)
    };
    options.UseNpgsql(connectionStringBuilder.ConnectionString);
});

// --- Modules ---
builder.Services.AddPlatformFoundationsHandlers();
builder.Services.AddIdentityHandlers();

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
app.UseAuthorization();

app.MapIdentityEndpoints();

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
