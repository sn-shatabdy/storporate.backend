using System.ComponentModel.DataAnnotations;

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
}
