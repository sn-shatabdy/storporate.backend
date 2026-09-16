using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.SecurityGovernance;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.SecurityGovernance.Querying;

/// <summary>
/// TDD coverage for <see cref="ListAuditLogEntriesHandler.ExecuteAsync"/>: filters apply
/// correctly, paging metadata is correct, and an unknown <c>sortBy</c> surfaces as a
/// validation error rather than a silent fallback or an unhandled exception.
/// </summary>
/// <remarks>
/// The test fixture uses the EF InMemory provider so the handler's LINQ composition
/// can be exercised without spinning up Postgres — the same pattern used by the
/// existing handler tests (see <c>Identity/ListSessionsHandlerTests</c>). Rows are
/// inserted directly via <see cref="DbContext.AddRange{TEntity}"/> rather than going
/// through <c>AuditLogWriter</c>, because the writer's hash-chain path is Postgres-only
/// (and is verified live elsewhere); the handler here is a plain EF Core read.
/// </remarks>
public class ListAuditLogEntriesHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_FilterByAction_NarrowsResultsToMatchingRowsOnly()
    {
        await using var dbContext = CreateDbContext();
        SeedEntries(dbContext,
            ("login_succeeded", "User"),
            ("login_succeeded", "User"),
            ("login_failed", "User"),
            ("permission_denied", "Permission"));

        var request = new ListAuditLogEntriesRequest { Action = "login_succeeded", PageSize = 50 };

        var page = await ListAuditLogEntriesHandler.ExecuteAsync(request, dbContext, CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, entry => Assert.Equal("login_succeeded", entry.Action));
    }

    [Fact]
    public async Task ExecuteAsync_FilterByDateRange_NarrowsResultsToMatchingRowsOnly()
    {
        await using var dbContext = CreateDbContext();

        var referenceNow = DateTime.UtcNow;
        var inRange = referenceNow.AddDays(-2);
        var tooEarly = referenceNow.AddDays(-10);
        // tooLate is one full day past the upper bound the request sets — guaranteed to
        // be filtered out regardless of test-execution latency between constructing the
        // seed times and the handler reading DateTime.UtcNow inside the query.
        var tooLate = referenceNow.AddHours(25);

        SeedEntry(dbContext, "login_succeeded", "User", createdAt: tooEarly);
        SeedEntry(dbContext, "login_succeeded", "User", createdAt: inRange);
        SeedEntry(dbContext, "login_succeeded", "User", createdAt: tooLate);
        SeedEntry(dbContext, "permission_denied", "Permission", createdAt: inRange);
        dbContext.SaveChanges();

        var request = new ListAuditLogEntriesRequest
        {
            FromDate = referenceNow.AddDays(-5),
            ToDate = referenceNow.AddHours(1),
            PageSize = 50,
        };

        var page = await ListAuditLogEntriesHandler.ExecuteAsync(request, dbContext, CancellationToken.None);

        // inRange row (login_succeeded) and inRange row (permission_denied) match; the
        // two out-of-range login_succeeded rows do not.
        Assert.Equal(2, page.TotalCount);
        Assert.All(page.Items, entry =>
        {
            Assert.True(
                entry.CreatedAt >= request.FromDate!.Value && entry.CreatedAt <= request.ToDate!.Value,
                $"Entry {entry.Id} createdAt {entry.CreatedAt:O} fell outside the requested range.");
        });
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task ExecuteAsync_FilterByFromDate_AcceptsNonLocalKindsAsAlreadyUtc(DateTimeKind kind)
    {
        // CreatedAt is timestamptz and the seeded rows are UTC. A caller that passes
        // DateTimeKind.Utc or DateTimeKind.Unspecified in their filter should have the
        // value compared verbatim against the column — only DateTimeKind.Local is
        // converted via ToUniversalTime(), which would otherwise silently shift the
        // comparison by the host's offset.
        await using var dbContext = CreateDbContext();
        var exactBoundary = new DateTime(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
        SeedEntry(dbContext, "above_boundary", "User", createdAt: exactBoundary.AddSeconds(1));
        SeedEntry(dbContext, "at_boundary", "User", createdAt: exactBoundary);
        SeedEntry(dbContext, "below_boundary", "User", createdAt: exactBoundary.AddSeconds(-1));
        dbContext.SaveChanges();

        var request = new ListAuditLogEntriesRequest
        {
            FromDate = DateTime.SpecifyKind(exactBoundary, kind),
            PageSize = 50,
        };

        var page = await ListAuditLogEntriesHandler.ExecuteAsync(request, dbContext, CancellationToken.None);

        Assert.Equal(2, page.TotalCount);
        Assert.DoesNotContain(page.Items, e => e.Action == "below_boundary");
    }

    [Fact]
    public async Task ExecuteAsync_FilterByFromDate_ConvertsLocalToUtcBeforeComparing()
    {
        // A caller that passes DateTimeKind.Local must have the filter shifted by the
        // local offset (TimeZoneInfo.Local.GetUtcOffset) before comparison, so a wall-clock
        // "9 AM local" filter on a server in UTC+2 looks for 7 AM UTC. The seeded row
        // uses an exact wall-clock value; we assert the row is found regardless of the
        // host's local offset, which only holds if the conversion is correct.
        await using var dbContext = CreateDbContext();
        // Build the seed time as UTC for a deterministic value.
        var seedUtc = new DateTime(2026, 9, 16, 7, 0, 0, DateTimeKind.Utc);
        SeedEntry(dbContext, "row_at_7_utc", "User", createdAt: seedUtc);
        SeedEntry(dbContext, "row_at_8_utc", "User", createdAt: seedUtc.AddHours(1));
        dbContext.SaveChanges();

        // Convert the same instant to a Local-kind DateTime. This represents what a
        // frontend sending a local wall-clock value would look like on a host in UTC+1
        // (so 7 AM UTC == 8 AM local == DateTimeKind.Local value of 8 AM).
        var localFilter = TimeZoneInfo.ConvertTimeFromUtc(seedUtc, TimeZoneInfo.Local);
        var request = new ListAuditLogEntriesRequest
        {
            FromDate = localFilter,
            PageSize = 50,
        };

        var page = await ListAuditLogEntriesHandler.ExecuteAsync(request, dbContext, CancellationToken.None);

        // The "row_at_7_utc" row matches only if the Local-kind filter was converted back
        // to UTC before comparison; otherwise the filter (e.g. "8 AM local" interpreted
        // as 8 AM UTC) would skip the 7 AM row entirely.
        Assert.Contains(page.Items, e => e.Action == "row_at_7_utc");
    }

    [Fact]
    public async Task ExecuteAsync_FilterByActionAndDateRange_IntersectsBoth()
    {
        await using var dbContext = CreateDbContext();

        var inRange = DateTime.UtcNow.AddDays(-1);
        SeedEntry(dbContext, "login_succeeded", "User", createdAt: inRange);
        SeedEntry(dbContext, "login_failed", "User", createdAt: inRange);
        SeedEntry(dbContext, "login_succeeded", "User", createdAt: DateTime.UtcNow.AddDays(-30));
        dbContext.SaveChanges();

        var request = new ListAuditLogEntriesRequest
        {
            Action = "login_succeeded",
            FromDate = DateTime.UtcNow.AddDays(-7),
            ToDate = DateTime.UtcNow,
            PageSize = 50,
        };

        var page = await ListAuditLogEntriesHandler.ExecuteAsync(request, dbContext, CancellationToken.None);

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("login_succeeded", page.Items[0].Action);
    }

    [Fact]
    public async Task ExecuteAsync_DefaultSortByCreatedAt_OrdersByCreatedAtAscendingStably()
    {
        await using var dbContext = CreateDbContext();

        var t1 = DateTime.UtcNow.AddMinutes(-3);
        var t2 = DateTime.UtcNow.AddMinutes(-2);
        var t3 = DateTime.UtcNow.AddMinutes(-1);
        SeedEntry(dbContext, "a", "User", createdAt: t1);
        SeedEntry(dbContext, "b", "User", createdAt: t2);
        SeedEntry(dbContext, "c", "User", createdAt: t3);
        dbContext.SaveChanges();

        var request = new ListAuditLogEntriesRequest { PageSize = 50 };

        var page = await ListAuditLogEntriesHandler.ExecuteAsync(request, dbContext, CancellationToken.None);

        // Default sort is createdAt ascending (oldest first) — SortMap's OrderBy default.
        // TieBreakBy(SequenceNumber) keeps the order stable across pages; the admin UI
        // can pass sortDescending=true if it wants most-recent-first.
        Assert.Equal(new[] { "a", "b", "c" }, page.Items.Select(e => e.Action).ToArray());
    }

    [Fact]
    public async Task ExecuteAsync_SortDescendingTrue_FlipsDefaultDirection()
    {
        await using var dbContext = CreateDbContext();

        var t1 = DateTime.UtcNow.AddMinutes(-3);
        var t2 = DateTime.UtcNow.AddMinutes(-2);
        var t3 = DateTime.UtcNow.AddMinutes(-1);
        SeedEntry(dbContext, "a", "User", createdAt: t1);
        SeedEntry(dbContext, "b", "User", createdAt: t2);
        SeedEntry(dbContext, "c", "User", createdAt: t3);
        dbContext.SaveChanges();

        var request = new ListAuditLogEntriesRequest { PageSize = 50, SortDescending = true };

        var page = await ListAuditLogEntriesHandler.ExecuteAsync(request, dbContext, CancellationToken.None);

        Assert.Equal(new[] { "c", "b", "a" }, page.Items.Select(e => e.Action).ToArray());
    }

    [Fact]
    public async Task ExecuteAsync_PaginationMetadata_IsCorrectAcrossMultiplePages()
    {
        await using var dbContext = CreateDbContext();
        for (var i = 0; i < 7; i++)
        {
            SeedEntry(dbContext, $"event_{i}", "User");
        }
        dbContext.SaveChanges();

        var firstRequest = new ListAuditLogEntriesRequest { PageNumber = 1, PageSize = 3 };
        var firstPage = await ListAuditLogEntriesHandler.ExecuteAsync(firstRequest, dbContext, CancellationToken.None);

        Assert.Equal(7, firstPage.TotalCount);
        Assert.Equal(3, firstPage.PageSize);
        Assert.Equal(1, firstPage.PageNumber);
        Assert.Equal(3, firstPage.TotalPages);
        Assert.False(firstPage.HasPrevious);
        Assert.True(firstPage.HasNext);
        Assert.Equal(3, firstPage.Items.Count);

        var thirdRequest = new ListAuditLogEntriesRequest { PageNumber = 3, PageSize = 3 };
        var thirdPage = await ListAuditLogEntriesHandler.ExecuteAsync(thirdRequest, dbContext, CancellationToken.None);

        Assert.Equal(3, thirdPage.PageNumber);
        Assert.True(thirdPage.HasPrevious);
        Assert.False(thirdPage.HasNext);
        // 7 total / 3 per page → third page has 1 row.
        Assert.Single(thirdPage.Items);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownSortBy_ThrowsUnknownSortKeyException()
    {
        await using var dbContext = CreateDbContext();
        SeedEntry(dbContext, "login_succeeded", "User");
        dbContext.SaveChanges();

        var request = new ListAuditLogEntriesRequest
        {
            SortBy = "totally_unknown_column",
            PageSize = 50,
        };

        var exception = await Assert.ThrowsAsync<UnknownSortKeyException>(() =>
            ListAuditLogEntriesHandler.ExecuteAsync(request, dbContext, CancellationToken.None));

        Assert.Equal("totally_unknown_column", exception.RequestedKey);
        // Every registered key (including the default) appears in the supported list so
        // the caller can self-correct from the error message alone.
        Assert.Contains("createdAt", exception.SupportedKeys);
        Assert.Contains("action", exception.SupportedKeys);
        Assert.Contains("resourceType", exception.SupportedKeys);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownSortBy_DoesNotFallBackSilently()
    {
        // Regression guard: an unknown sort key must not be silently rewritten to the
        // default — the page must not come back with results as if the caller had passed
        // a valid sortBy. Throwing the exception is the only correct behavior; the test
        // below just confirms no items are returned on the throw path.
        await using var dbContext = CreateDbContext();
        SeedEntry(dbContext, "login_succeeded", "User");
        dbContext.SaveChanges();

        var request = new ListAuditLogEntriesRequest
        {
            SortBy = "garbage",
            PageSize = 50,
        };

        await Assert.ThrowsAsync<UnknownSortKeyException>(() =>
            ListAuditLogEntriesHandler.ExecuteAsync(request, dbContext, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_ResponseOmitsHashAndPreviousHash()
    {
        // The DTO deliberately omits the hash-chain fields so they stay in the DB for
        // STOR-45's Integrity Layer. The test locks that contract down — a future
        // refactor that adds the fields back to the response would silently leak them
        // to every admin browser without anyone noticing otherwise.
        await using var dbContext = CreateDbContext();
        SeedEntry(dbContext, "login_succeeded", "User");
        dbContext.SaveChanges();

        var request = new ListAuditLogEntriesRequest { PageSize = 50 };

        var page = await ListAuditLogEntriesHandler.ExecuteAsync(request, dbContext, CancellationToken.None);

        var responseType = typeof(AuditLogEntryResponse);
        Assert.Null(responseType.GetProperty("Hash"));
        Assert.Null(responseType.GetProperty("PreviousHash"));
    }

    private static void SeedEntries(
        WriteDbContext dbContext,
        params (string Action, string ResourceType)[] rows)
    {
        var now = DateTime.UtcNow;
        foreach (var (action, resourceType) in rows)
        {
            SeedEntry(dbContext, action, resourceType, createdAt: now);
        }

        dbContext.SaveChanges();
    }

    private static void SeedEntry(
        WriteDbContext dbContext,
        string action,
        string resourceType,
        DateTime? createdAt = null)
    {
        dbContext.AuditLogEntries.Add(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            // SequenceNumber is allocated server-side by Postgres' identity-always
            // column; under the InMemory provider we set it manually so the
            // TieBreakBy(s => s.SequenceNumber) ordering has deterministic values.
            SequenceNumber = NextSequenceNumber++,
            AccountId = Guid.NewGuid(),
            ActorUserId = Guid.NewGuid(),
            Action = action,
            ResourceType = resourceType,
            ResourceId = Guid.NewGuid().ToString(),
            IpAddress = "127.0.0.1",
            UserAgent = "test-agent",
            MetadataJson = null,
            CreatedAt = createdAt ?? DateTime.UtcNow,
            Hash = new string('h', 64),
            PreviousHash = null,
        });
    }

    // Static counter so every SeedEntry call gets a strictly-increasing SequenceNumber
    // — independent of insertion order — so the TieBreakBy(s => s.SequenceNumber) sort
    // path has a deterministic secondary key even when two rows share a createdAt.
    private static long NextSequenceNumber = 1;

    private static WriteDbContext CreateDbContext() =>
        new(
            new DbContextOptionsBuilder<WriteDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options,
            new AmbientAccountContext());
}
