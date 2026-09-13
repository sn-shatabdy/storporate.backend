using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Storporate.Api.Configuration;
using Storporate.Api.Errors;
using Storporate.Infrastructure.Llm;
using Storporate.Infrastructure.Persistence;
using Storporate.Infrastructure.Security;
using Storporate.Infrastructure.Storage;
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

// --- AI provider (Bionic-hosted local LLM, OpenAI-compatible) ---
builder.Services.AddBionicLlmProvider();

// --- Artifact storage (S3-compatible; local MinIO now, real Cloudflare R2 later) ---
builder.Services.AddArtifactStorage();

// --- FluentValidation: one validator per use case, discovered by scanning the Modules assembly ---
builder.Services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);

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
