using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Authorization;

/// <summary>
/// Populates the ambient <see cref="IAccountContext"/> (UserId, AccountId, IsAdministrator,
/// IpAddress, UserAgent) for the current request. Runs after <c>UseAuthentication()</c> so the
/// JWT bearer middleware has already validated the access token and built
/// <see cref="HttpContext.User"/>; runs before <c>UseAuthorization()</c> so every downstream
/// <c>[Authorize]</c> / <c>RequirePermission</c> gate sees the resolved account context.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="UserId"/> is read from the JWT <c>sub</c> claim (RFC 7519 §4.1.2). The host's
/// <c>AddJwtBearer</c> configuration sets <c>MapInboundClaims = false</c> exactly so this
/// lookup sees the raw claim type — see <c>Program.cs</c>'s doc comment on that flag for
/// why re-mapping would silently break the lookup.
/// </para>
/// <para>
/// <see cref="IAccountContext.IsAdministrator"/> is read from the JWT <c>actor_type</c>
/// claim (issued by <c>JwtTokenService.CreateAccessToken</c>) rather than a
/// <see cref="Infrastructure.Persistence.WriteDbContext"/> lookup of <see cref="User.ActorType"/>.
/// The <c>actor_type</c> claim is part of the same signed token, so trusting it costs no
/// extra DB round-trip and stays accurate even if the user's role is changed between token
/// issuance and request — an Administrator demoted after issuing an access token simply
/// loses the bypass on the next token refresh, which is the right behavior for a short-
/// lived (<c>JwtOptions.AccessTokenMinutes</c>) access token. A DB lookup would either
/// have to be cached per-token (defeating the freshness goal) or repeated per request
/// (wasting a round-trip on every authenticated call).
/// </para>
/// <para>
/// <see cref="IAccountContext.AccountId"/> is read from the matched route's
/// <c>{accountId}</c> route value, when one is present. Endpoints that don't bind
/// <c>{accountId}</c> — every self-service endpoint that operates on the caller's own
/// data — leave <see cref="IAccountContext.AccountId"/> as <see langword="null"/>, which the
/// downstream layers treat as "implicitly the caller's own account" (the same-id check in
/// the global query filter / RLS policy resolves this against <see cref="IAccountContext.UserId"/>
/// when it runs).
/// </para>
/// <para>
/// <see cref="IAccountContext.IpAddress"/> and <see cref="IAccountContext.UserAgent"/> are
/// captured once here, at the start of the pipeline, so the audit trail
/// (<c>AuditLogWriter</c>) can stamp them into each row's hash without re-reading the
/// <see cref="HttpContext"/>, which isn't easily accessible from a raw-SQL writer running
/// outside EF Core. The <c>User-Agent</c> header is truncated to
/// <see cref="UserAgentMaxLength"/> characters here to match the
/// <c>AuditLogEntries.UserAgent</c> column width — truncating at capture time means the
/// hash always covers exactly the bytes that land in the DB, with no surprise truncation
/// at write time.
/// </para>
/// </remarks>
public static class AccountContextMiddleware
{
    private const string AccountIdRouteParameterName = "accountId";

    private const string ActorTypeClaimType = "actor_type";

    public static IApplicationBuilder UseAccountContext(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var writer = context.RequestServices
                .GetRequiredService<IAccountContextWriter>();

            var subClaim = context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            if (Guid.TryParse(subClaim, out var userId))
            {
                writer.SetUserId(userId);
            }

            var actorTypeClaim = context.User.FindFirst(ActorTypeClaimType)?.Value;
            writer.SetIsAdministrator(string.Equals(actorTypeClaim, ActorTypes.Administrator, StringComparison.Ordinal));

            if (context.GetRouteValue(AccountIdRouteParameterName) is string accountIdText
                && Guid.TryParse(accountIdText, out var accountId))
            {
                writer.SetAccountId(accountId);
            }

            // RemoteIpAddress reflects the direct TCP peer (a reverse proxy's address,
            // not the client's, once hosted behind one — noted in the STOR-63 plan as a
            // future gap, not solved here). User-Agent is truncated to UserAgentMaxLength
            // so the captured value is exactly the bytes the column can hold; absent or
            // empty headers coerce to null so the audit row doesn't carry a meaningless
            // empty string.
            writer.SetIpAddress(context.Connection.RemoteIpAddress?.ToString());

            var rawUserAgent = context.Request.Headers.UserAgent.ToString();
            if (string.IsNullOrEmpty(rawUserAgent))
            {
                writer.SetUserAgent(null);
            }
            else if (rawUserAgent.Length > AuditLogEntry.UserAgentMaxLength)
            {
                writer.SetUserAgent(rawUserAgent[..AuditLogEntry.UserAgentMaxLength]);
            }
            else
            {
                writer.SetUserAgent(rawUserAgent);
            }

            await next(context);
        });
}
