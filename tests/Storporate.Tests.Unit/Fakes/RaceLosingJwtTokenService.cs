using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.Fakes;

/// <summary>
/// Simulates the "race loser" outcome of <see cref="IJwtTokenService.IssueRotatedTokensAsync"/>:
/// always returns <c>null</c>, mirroring the production behaviour of the real
/// <c>JwtTokenService</c> when its atomic <c>ExecuteUpdateAsync</c> claim sees
/// <c>ReplacedBySessionId</c> already set (the row was claimed by a concurrent caller first).
/// <see cref="RefreshSessionHandler"/> must treat this identically to an explicit reuse/theft
/// signal — revoke the entire family and throw <c>RefreshTokenReusedException</c>.
/// </summary>
public sealed class RaceLosingJwtTokenService : IJwtTokenService
{
    public int IssueRotatedCalls { get; private set; }

    public Task<AuthTokenResult> IssueTokensAsync(
        User user,
        string? userAgent,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AuthTokenResult(
            "should-not-be-called",
            DateTime.UtcNow.AddMinutes(15),
            "should-not-be-called",
            DateTime.UtcNow.AddDays(7)));

    public Task<AuthTokenResult?> IssueRotatedTokensAsync(
        User user,
        string? userAgent,
        Guid familyId,
        Guid replacedSessionId,
        CancellationToken cancellationToken = default)
    {
        IssueRotatedCalls++;
        return Task.FromResult<AuthTokenResult?>(null);
    }
}