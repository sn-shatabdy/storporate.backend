namespace Storporate.Api.Errors;

/// <summary>The uniform error body returned by every failure path in the API.</summary>
public sealed record ErrorResponse(string ErrorCode, string Message);
