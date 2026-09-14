using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.Fakes;

/// <summary>
/// Deterministic <see cref="IJwtTokenService"/> for handler tests: always returns the same
/// access/refresh token strings, generated with a unique suffix per call so tests can prove
/// rotation produced a new pair.
/// </summary>
public sealed class FakeJwtTokenService : IJwtTokenService
{
    private int _invocationCount;

    public int IssueCount => _invocationCount;

    public List<AuthTokenResult> IssuedTokens { get; } = [];

    public Task<AuthTokenResult> IssueTokensAsync(
        User user,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        _invocationCount++;
        var now = DateTime.UtcNow;
        var result = new AuthTokenResult(
            AccessToken: $"fake-access-{_invocationCount}",
            AccessTokenExpiresAt: now.AddMinutes(15),
            RefreshToken: $"fake-refresh-{_invocationCount}",
            RefreshTokenExpiresAt: now.AddDays(7));
        IssuedTokens.Add(result);
        return Task.FromResult(result);
    }

    public Task<AuthTokenResult?> IssueRotatedTokensAsync(
        User user,
        string? userAgent,
        Guid familyId,
        Guid replacedSessionId,
        CancellationToken cancellationToken = default)
    {
        _invocationCount++;
        var now = DateTime.UtcNow;
        var result = new AuthTokenResult(
            AccessToken: $"fake-access-rotated-{_invocationCount}",
            AccessTokenExpiresAt: now.AddMinutes(15),
            RefreshToken: $"fake-refresh-rotated-{_invocationCount}",
            RefreshTokenExpiresAt: now.AddDays(7));
        IssuedTokens.Add(result);
        return Task.FromResult<AuthTokenResult?>(result);
    }
}
