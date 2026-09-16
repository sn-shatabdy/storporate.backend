using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Authorization;

namespace Storporate.Modules.SecurityGovernance;

/// <summary>
/// Minimal-API endpoints for the SecurityGovernance module — the Administrator-only
/// audit-log query that reads the rows written by the auth/session instrumentation.
/// Registered once from <c>Program.cs</c> via <c>app.MapAuditLogEndpoints()</c>.
/// </summary>
public static class AuditLogEndpoints
{
    /// <summary>
    /// Maps the audit-log query endpoint onto the supplied <paramref name="app"/>. Each
    /// query-string field is a nullable primitive rather than a <c>[AsParameters]</c> record
    /// so the minimal-API binder keeps <see cref="ListAuditLogEntriesRequest"/>'s abstract
    /// <c>PageRequest</c> base clean (parameters don't need an inheritance walk).
    /// </summary>
    public static void MapAuditLogEndpoints(this WebApplication app)
    {
        app.MapGet("/api/security-governance/audit-log", async (
                int? pageNumber,
                int? pageSize,
                string? sortBy,
                bool? sortDescending,
                string? action,
                string? resourceType,
                Guid? accountId,
                Guid? actorUserId,
                DateTime? fromDate,
                DateTime? toDate,
                WriteDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var request = new ListAuditLogEntriesRequest
                {
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    SortBy = sortBy,
                    SortDescending = sortDescending ?? false,
                    Action = action,
                    ResourceType = resourceType,
                    AccountId = accountId,
                    ActorUserId = actorUserId,
                    FromDate = fromDate,
                    ToDate = toDate,
                };

                var page = await ListAuditLogEntriesHandler.ExecuteAsync(
                    request,
                    dbContext,
                    cancellationToken);
                return Results.Ok(page);
            })
            .RequirePermission(Permissions.SecurityGovernance.ViewAuditLog);
    }
}
