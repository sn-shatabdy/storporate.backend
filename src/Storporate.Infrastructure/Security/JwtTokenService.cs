using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Abstractions;
using Storporate.SharedKernel.Entities;
using Storporate.SharedKernel.Security;

namespace Storporate.Infrastructure.Security;

/// <summary>
/// <see cref="IJwtTokenService"/> backed by a symmetric HMAC-SHA256-signed JWT access token
/// (claims: user id, email, actor type, verification status) and a cryptographically random
/// opaque refresh token. The refresh token's plaintext is returned to the caller exactly once, at
/// issuance — <see cref="Sha256CodeHasher"/> hashes it before it is persisted as a new
/// <see cref="Session"/> row (fresh <see cref="Session.FamilyId"/> per issuance), the same
/// one-way-hash-at-rest treatment already used for OTP codes.
/// </summary>
public sealed class JwtTokenService(
    WriteDbContext dbContext,
    IOptions<JwtOptions> options) : IJwtTokenService
{
    /// <summary>256 bits — generous relative to <see cref="JwtOptions.MinimumSigningKeyLengthBytes"/>,
    /// matching the entropy of the JWT signing key itself.</summary>
    private const int RefreshTokenByteLength = 32;

    public async Task<AuthTokenResult> IssueTokensAsync(
        User user,
        string? userAgent,
        CancellationToken cancellationToken = default)
    {
        var jwtOptions = options.Value;
        var now = DateTime.UtcNow;

        var accessTokenExpiresAt = now.AddMinutes(jwtOptions.AccessTokenMinutes);
        var refreshToken = GenerateOpaqueRefreshToken();
        var refreshTokenExpiresAt = now.AddDays(jwtOptions.RefreshTokenDays);

        var session = new Session
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            HashedRefreshToken = Sha256CodeHasher.Hash(refreshToken),
            FamilyId = Guid.NewGuid(),
            ExpiresAt = refreshTokenExpiresAt,
            UserAgent = userAgent,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var accessToken = CreateAccessToken(user, jwtOptions, now, accessTokenExpiresAt, session.Id);

        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AuthTokenResult(accessToken, accessTokenExpiresAt, refreshToken, refreshTokenExpiresAt);
    }

    public async Task<AuthTokenResult> IssueRotatedTokensAsync(
        User user,
        string? userAgent,
        Guid familyId,
        Guid replacedSessionId,
        CancellationToken cancellationToken = default)
    {
        var jwtOptions = options.Value;
        var now = DateTime.UtcNow;

        var accessTokenExpiresAt = now.AddMinutes(jwtOptions.AccessTokenMinutes);
        var refreshToken = GenerateOpaqueRefreshToken();
        var refreshTokenExpiresAt = now.AddDays(jwtOptions.RefreshTokenDays);

        // New row lives in the same FamilyId so "log out everywhere" / theft-detection can
        // revoke the whole chain in one UPDATE. The OLD session's ReplacedBySessionId points
        // back at the new session id, so presenting the rotated-out token again is detectable
        // as reuse/theft (see RefreshSessionHandler).
        var newSessionId = Guid.NewGuid();
        var session = new Session
        {
            Id = newSessionId,
            UserId = user.Id,
            HashedRefreshToken = Sha256CodeHasher.Hash(refreshToken),
            FamilyId = familyId,
            ExpiresAt = refreshTokenExpiresAt,
            UserAgent = userAgent,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var replacedSession = await dbContext.Sessions.FirstOrDefaultAsync(
            s => s.Id == replacedSessionId,
            cancellationToken);
        if (replacedSession is null)
        {
            throw new InvalidOperationException(
                $"Session to be replaced ({replacedSessionId}) was not found in the database.");
        }

        replacedSession.ReplacedBySessionId = newSessionId;
        replacedSession.UpdatedAt = now;

        var accessToken = CreateAccessToken(user, jwtOptions, now, accessTokenExpiresAt, newSessionId);

        dbContext.Sessions.Add(session);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new AuthTokenResult(accessToken, accessTokenExpiresAt, refreshToken, refreshTokenExpiresAt);
    }

    private static string CreateAccessToken(User user, JwtOptions jwtOptions, DateTime now, DateTime expiresAt, Guid sessionId)
    {
        // "sid" is the standard JWT RFC 7519 §4.1.6 session id claim — carrying the Session.Id
        // here is what lets the [Authorize] endpoints (/me, /logout, /logout-all) know which
        // Session row this access token belongs to, without forcing the caller to also send a
        // refresh token in the body of logout.
        Claim[] claims =
        [
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("actor_type", user.ActorType),
            new Claim("verification_status", user.VerificationStatus),
            new Claim(JwtRegisteredClaimNames.Sid, sessionId.ToString()),
        ];

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.SigningKey));
        var signingCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: jwtOptions.Issuer,
            audience: jwtOptions.Audience,
            claims: claims,
            notBefore: now,
            expires: expiresAt,
            signingCredentials: signingCredentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Base64url (no padding) so the token is transport-safe in headers/query strings
    /// without extra encoding, matching the shape confirmed working in ~/AIT/Docomate's own
    /// <c>JwtTokenService.GenerateRefreshToken</c>.</summary>
    private static string GenerateOpaqueRefreshToken()
    {
        var randomBytes = RandomNumberGenerator.GetBytes(RefreshTokenByteLength);
        return Convert.ToBase64String(randomBytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
