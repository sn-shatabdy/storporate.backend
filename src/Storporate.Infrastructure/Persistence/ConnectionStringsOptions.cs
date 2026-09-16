using System.ComponentModel.DataAnnotations;
using Npgsql;

namespace Storporate.Infrastructure.Persistence;

/// <summary>
/// Bound to the standard ASP.NET Core "ConnectionStrings" configuration section. Validated
/// eagerly at startup via <c>.ValidateOnStart()</c> so a missing connection string fails fast
/// instead of surfacing as a null-reference the first time a request touches the database.
/// </summary>
public sealed class ConnectionStringsOptions
{
    public const string SectionName = "ConnectionStrings";

    [Required]
    public string WriteDb { get; init; } = string.Empty;

    /// <summary>
    /// Npgsql SSL negotiation mode applied to <see cref="WriteDb"/> at startup. Defaults to
    /// "Prefer", which negotiates plaintext against today's non-TLS local Docker Postgres
    /// container (a no-op) and becomes a real enforcement point (e.g. "Require"/"VerifyFull")
    /// once pointed at a hosted Postgres later. Must be one of Npgsql's
    /// <see cref="Npgsql.SslMode"/> enum names.
    /// </summary>
    [AllowedValues("Disable", "Allow", "Prefer", "Require", "VerifyCA", "VerifyFull")]
    public string SslMode { get; init; } = "Prefer";

    /// <summary>EF Core provider invariant name the host is configured against. Used by
    /// <c>AuditLogWriter</c> to gate the Postgres-only hash-chain path — under any other
    /// provider (notably the EF InMemory provider the unit-test suite uses) the writer
    /// becomes a no-op.</summary>
    public string WriteDbProviderName { get; init; } = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>
    /// Returns <see cref="WriteDb"/> with the configured <see cref="SslMode"/> applied so
    /// callers using the raw connection string (notably <c>AuditLogWriter</c> opening its
    /// own <see cref="NpgsqlConnection"/>) agree with EF Core on SSL negotiation.
    /// </summary>
    public string ToNpgsqlConnectionString() =>
        new NpgsqlConnectionStringBuilder(WriteDb)
        {
            SslMode = Enum.Parse<SslMode>(SslMode, ignoreCase: true),
        }.ConnectionString;
}

