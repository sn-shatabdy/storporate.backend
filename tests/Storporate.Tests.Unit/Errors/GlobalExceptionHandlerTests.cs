using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Storporate.Api.Errors;
using Storporate.Modules.Identity.Exceptions;

namespace Storporate.Tests.Unit.Errors;

/// <summary>
/// Verifies STOR-61's five new Identity exception types each map to the HTTP status and
/// <see cref="ErrorResponse"/> shape specified in the plan, directly against
/// <see cref="GlobalExceptionHandler"/> — no HTTP server or throwaway diagnostics endpoint
/// required.
/// </summary>
public class GlobalExceptionHandlerTests
{
    private static readonly GlobalExceptionHandler Handler = new(
        NullLogger<GlobalExceptionHandler>.Instance,
        new FakeHostEnvironment());

    // HttpResponse.WriteAsJsonAsync falls back to camelCase (JsonSerializerDefaults.Web) when no
    // DI-configured JsonOptions are resolvable from HttpContext.RequestServices, as is the case
    // for the bare DefaultHttpContext used in these tests — mirror that here to read the body
    // back, rather than asserting against a serializer configuration the handler doesn't use.
    private static readonly JsonSerializerOptions ResponseJsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [MemberData(nameof(IdentityExceptionCases))]
    public async Task TryHandleAsync_WithIdentityException_MapsToExpectedStatusAndErrorCode(
        Exception exception,
        int expectedStatusCode,
        string expectedErrorCode)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();

        var handled = await Handler.TryHandleAsync(httpContext, exception, CancellationToken.None);

        Assert.True(handled);
        Assert.Equal(expectedStatusCode, httpContext.Response.StatusCode);

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var body = await JsonSerializer.DeserializeAsync<ErrorResponse>(httpContext.Response.Body, ResponseJsonOptions);

        Assert.NotNull(body);
        Assert.Equal(expectedErrorCode, body!.ErrorCode);
        Assert.Equal(exception.Message, body.Message);
    }

    public static IEnumerable<object[]> IdentityExceptionCases()
    {
        yield return [new OtpInvalidException(), StatusCodes.Status400BadRequest, "otp_invalid"];
        yield return [new OtpLockedException(), StatusCodes.Status401Unauthorized, "otp_locked"];
        yield return
        [
            new OtpRateLimitExceededException(), StatusCodes.Status429TooManyRequests, "otp_rate_limit_exceeded",
        ];
        yield return
        [
            new EmailAlreadyRegisteredException(), StatusCodes.Status409Conflict, "email_already_registered",
        ];
        yield return
        [
            new RefreshTokenReusedException(), StatusCodes.Status401Unauthorized, "refresh_token_reused",
        ];
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;

        public string ApplicationName { get; set; } = "Storporate.Tests.Unit";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
