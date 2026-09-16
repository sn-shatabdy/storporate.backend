using Storporate.SharedKernel.Pagination;

namespace Storporate.Modules.Portfolio;

/// <summary>
/// Pagination-only request for <c>GET /api/portfolio/items</c>. Inherits
/// <see cref="PageRequest"/>'s four pagination fields (<c>pageNumber</c>,
/// <c>pageSize</c>, <c>sortBy</c>, <c>sortDescending</c>) and adds no extra filters
/// for STOR-37 Phase 1 — the tenant scope (caller's own
/// <see cref="Storporate.SharedKernel.Entities.PortfolioItem"/> rows only) is enforced
/// by the global query filter on <see cref="Storporate.SharedKernel.Entities.IAccountScoped"/>,
/// so a flat pagination surface is enough to drive the student's "my portfolio" list.
/// </summary>
/// <remarks>
/// Future story may add a <c>category</c> filter or a date-range bound; for now the
/// student page just sorts newest-first and shows whatever the caller's account owns.
/// </remarks>
public sealed record ListPortfolioItemsRequest : PageRequest;