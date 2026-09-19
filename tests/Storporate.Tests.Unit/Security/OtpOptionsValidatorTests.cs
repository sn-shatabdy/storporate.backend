using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Security;
using Storporate.Tests.Unit.Fakes;

namespace Storporate.Tests.Unit.Security;

/// <summary>
/// Direct coverage for <see cref="OtpOptionsValidator"/>: the dev-only shape gate
/// that fails the host on startup when a configured <see cref="OtpOptions.MasterCode"/>
/// doesn't match <see cref="OtpOptions.CodeLength"/> or contains non-digits. Lives
/// here (rather than alongside the existing VerifyOtpHandler tests) because the
/// validator runs in <c>Program.cs</c>'s options pipeline, independent of the
/// per-request OTP flow — same convention as <c>JwtOptionsTests</c>.
/// </summary>
public class OtpOptionsValidatorTests
{
    private static OtpOptionsValidator ValidatorForEnvironment(string environmentName) =>
        new(new FakeHostEnvironment(environmentName));

    [Fact]
    public void Validate_InDevelopment_MasterCodeLengthMismatch_Fails()
    {
        // CodeLength default is 6 digits. A 4-digit master code would be
        // rejected by VerifyOtpValidator's Length(codeLength) check before
        // the bypass branch could fire, silently disabling the dev bypass.
        var validator = ValidatorForEnvironment(FakeHostEnvironment.DevelopmentEnvironmentName);
        var options = new OtpOptions { MasterCode = "0000" };

        var result = validator.Validate(OtpOptions.SectionName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("length", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_InDevelopment_MasterCodeContainsNonDigits_Fails()
    {
        // Same trap, different shape: a letter in the master code would be
        // rejected by VerifyOtpValidator's Matches("^[0-9]+$") check.
        var validator = ValidatorForEnvironment(FakeHostEnvironment.DevelopmentEnvironmentName);
        var options = new OtpOptions { MasterCode = "00000A" };

        var result = validator.Validate(OtpOptions.SectionName, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, f => f.Contains("digit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_InDevelopment_MasterCodeIsAllDigitsAndCorrectLength_Succeeds()
    {
        // Happy path: 6 all-digits with default CodeLength 6.
        var validator = ValidatorForEnvironment(FakeHostEnvironment.DevelopmentEnvironmentName);
        var options = new OtpOptions { MasterCode = "000000" };

        var result = validator.Validate(OtpOptions.SectionName, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_InProduction_MasterCodeSet_StillSucceeds()
    {
        // The validator is gated by IsDevelopment(): in Production the bypass
        // is unconditionally off (VerifyOtpHandler enforces this at runtime),
        // so the validator must short-circuit Success regardless of MasterCode
        // shape. A Production deploy with a misconfigured MasterCode should
        // not crash the host.
        var validator = ValidatorForEnvironment(FakeHostEnvironment.ProductionEnvironmentName);
        var options = new OtpOptions { MasterCode = "not-a-real-code" };

        var result = validator.Validate(OtpOptions.SectionName, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_MasterCodeIsNull_EvenInDevelopment_Succeeds()
    {
        // MasterCode null means the bypass is fully off; the validator must
        // not require a value (only validate when one IS configured).
        var validator = ValidatorForEnvironment(FakeHostEnvironment.DevelopmentEnvironmentName);
        var options = new OtpOptions { MasterCode = null };

        var result = validator.Validate(OtpOptions.SectionName, options);

        Assert.True(result.Succeeded);
    }
}
