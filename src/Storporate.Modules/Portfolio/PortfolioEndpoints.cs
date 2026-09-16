using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel.Authorization;
using Storporate.SharedKernel.Storage;

namespace Storporate.Modules.Portfolio;

/// <summary>
/// Minimal-API endpoints for the Portfolio module — student-facing capture,
/// list, and hard-delete of portfolio rows. Registered once from
/// <c>Program.cs</c> via <c>app.MapPortfolioEndpoints()</c> alongside the
/// other module endpoint maps.
/// </summary>
/// <remarks>
/// <para>
/// <b>Multipart binding.</b> The create handler binds <c>[FromForm]</c> on every
/// form field (and the file itself), so the minimal-API binder treats the request
/// as one multipart body rather than guessing — see the
/// <c>minimal-api-file-upload</c> skill's "IFormFile + other form fields" rule.
/// The validator runs against <see cref="CreatePortfolioItemRequest"/> with the bound
/// values and rejects oversized / disallowed / conflicting payloads before any
/// storage write or DB insert runs.
/// </para>
/// <para>
/// <b>Permission gates.</b> Each endpoint calls
/// <c>.RequirePermission(Permissions.Portfolio.&lt;Verb&gt;)</c>, mirroring
/// the established pattern from
/// <see cref="Storporate.Modules.SecurityGovernance.AuditLogEndpoints"/>'s
/// <see cref="RequirePermissionExtensions.RequirePermission"/> usage. The
/// three new <see cref="Permissions.Portfolio"/> constants are recognized by
/// the <see cref="RequirePermissionAttribute"/> fail-fast check because
/// <see cref="Permissions.All"/> is rebuilt via reflection.
/// </para>
/// <para>
/// <b>Tenant isolation.</b> The list / delete handlers rely on the EF Core global
/// query filter installed in <see cref="WriteDbContext.OnModelCreating"/> for
/// every <see cref="Storporate.SharedKernel.Entities.IAccountScoped"/> entity —
/// <see cref="Storporate.SharedKernel.Entities.PortfolioItem"/> implements it — so
/// reads are scoped to the ambient account automatically. The delete handler
/// treats "row doesn't match the filter" as a 404 rather than 403 to avoid
/// confirming another account's portfolio id exists.
/// </para>
/// </remarks>
public static class PortfolioEndpoints
{
    public static void MapPortfolioEndpoints(this WebApplication app)
    {
        app.MapPost("/api/portfolio/items", async (
                HttpContext httpContext,
                IValidator<CreatePortfolioItemRequest> validator,
                WriteDbContext dbContext,
                IArtifactStore artifactStore,
                IAccountContext accountContext,
                CancellationToken cancellationToken) =>
            {
                // Bind the multipart form body into the request record. The
                // [FromForm] attribute is unnecessary on the parameters because
                // every binding here is a form field or IFormFile — the binder
                // recognizes the multipart shape on its own. Reading the form
                // explicitly (rather than via a [FromForm] parameter) keeps
                // the call site independent of how the type's properties are
                // declared, which is exactly what the minimal-api-file-upload
                // skill recommends for "file + other fields in one endpoint".
                var form = await httpContext.Request.ReadFormAsync(cancellationToken).ConfigureAwait(false);

                var file = form.Files.GetFile("file") ?? form.Files.FirstOrDefault();
                var request = new CreatePortfolioItemRequest
                {
                    Label = form["label"].ToString(),
                    Category = form["category"].ToString(),
                    CustomCategoryText = form["customCategoryText"].ToString(),
                    Description = form["description"].ToString(),
                    ExternalUrl = form["externalUrl"].ToString(),
                    File = file,
                };

                await validator.ValidateAndThrowAsync(request, cancellationToken).ConfigureAwait(false);

                var response = await CreatePortfolioItemHandler.ExecuteAsync(
                    request,
                    dbContext,
                    artifactStore,
                    accountContext,
                    cancellationToken).ConfigureAwait(false);

                return Results.Created($"/api/portfolio/items/{response.Id}", response);
            })
            .RequirePermission(Permissions.Portfolio.Create);

        app.MapGet("/api/portfolio/items", async (
                int? pageNumber,
                int? pageSize,
                string? sortBy,
                bool? sortDescending,
                WriteDbContext dbContext,
                CancellationToken cancellationToken) =>
            {
                var request = new ListPortfolioItemsRequest
                {
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    SortBy = sortBy,
                    SortDescending = sortDescending ?? false,
                };

                var page = await ListPortfolioItemsHandler.ExecuteAsync(
                    request,
                    dbContext,
                    cancellationToken).ConfigureAwait(false);

                return Results.Ok(page);
            })
            .RequirePermission(Permissions.Portfolio.Read);

        app.MapDelete("/api/portfolio/items/{id:guid}", async (
                Guid id,
                WriteDbContext dbContext,
                IArtifactStore artifactStore,
                IAuditLogWriter auditLogWriter,
                CancellationToken cancellationToken) =>
            {
                var deleted = await DeletePortfolioItemHandler.ExecuteAsync(
                    id,
                    dbContext,
                    artifactStore,
                    auditLogWriter,
                    cancellationToken).ConfigureAwait(false);

                return deleted
                    ? Results.NoContent()
                    : Results.NotFound();
            })
            .RequirePermission(Permissions.Portfolio.Delete);
    }
}