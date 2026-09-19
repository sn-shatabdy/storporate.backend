using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Storporate.Infrastructure.Security;

/// <summary>
/// Configuration for OTP generation/expiry, bound to the "Otp" section. Every numeric knob here
/// is operator-tunable via environment variables rather than a hardcoded constant (see the
/// plan's rate-limit-numbers-are-flexible risk mitigation), with defaults matching the plan's
/// Phase 2 spec exactly (6-digit code, 10-minute expiry, 5 max attempts).
/// </summary>
public sealed class OtpOptions
{
    public const string SectionName = "Otp";

    [Range(4, 10)]
    public int CodeLength { get; init; } = 6;

    [Range(1, int.MaxValue)]
    public int ExpiryMinutes { get; init; } = 10;

    [Range(1, int.MaxValue)]
    public int MaxAttempts { get; init; } = 5;

    /// <summary>
    /// A fixed code that, when <see cref="IHostEnvironment.IsDevelopment"/>
    /// is true, bypasses OTP lookup/expiry/attempt checks entirely for any email. Must be null in every
    /// non-Development environment — only ever set in appsettings.Development.json, never in the base
    /// appsettings.json. Two independent gates (config presence + runtime environment check) exist
    /// specifically so this can never activate outside local development.
    /// </summary>
    public string? MasterCode { get; init; }
}

/// <summary>
/// <see cref="IValidateOptions{TOptions}"/> that enforces the shape contract on
/// <see cref="OtpOptions.MasterCode"/> when the code is configured: the value must be exactly
/// <see cref="OtpOptions.CodeLength"/> characters long and contain only digits, so a developer
/// can't misconfigure it to something that would be rejected by <c>VerifyOtpValidator</c> as
/// invalid input (which would silently disable the dev-only bypass). The check only runs when
/// <see cref="IHostEnvironment.IsDevelopment"/> is true AND <see cref="OtpOptions.MasterCode"/>
/// is non-null/non-empty — Production environments ignore the field entirely and stay silent.
/// Wired from <c>Program.cs</c> as an <c>IValidateOptions&lt;OtpOptions&gt;</c> singleton.
/// </summary>
public sealed class OtpOptionsValidator : IValidateOptions<OtpOptions>
{
    private readonly IHostEnvironment _environment;

    public OtpOptionsValidator(IHostEnvironment environment)
    {
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, OtpOptions options)
    {
        if (!_environment.IsDevelopment() || string.IsNullOrEmpty(options.MasterCode))
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        if (options.MasterCode.Length != options.CodeLength)
        {
            failures.Add(
                $"Otp:MasterCode length ({options.MasterCode.Length}) must equal CodeLength ({options.CodeLength}).");
        }
        if (!options.MasterCode.All(char.IsDigit))
        {
            failures.Add("Otp:MasterCode must contain only digits.");
        }
        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
