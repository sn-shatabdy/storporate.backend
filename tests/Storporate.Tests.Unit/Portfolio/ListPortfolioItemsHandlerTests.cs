using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.Portfolio;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.Portfolio;

/// <summary>
/// TDD coverage for <see cref="ListPortfolioItemsHandler"/>: the EF InMemory provider
/// respects the <see cref="IEntityTypeConfiguration{HasQueryFilter}"/> lambda
/// that <see cref="WriteDbContext.OnModelCreating"/> installs for every
/// <see cref="IAccountScoped"/> entity, so seeding via the same
/// <see cref="WriteDbContext"/> is enough to verify the "list only returns the
/// caller's own items" acceptance criterion (the production
/// <see cref="AmbientAccountContext.AccountId"/> flows through the same filter
/// in both code paths).
/// </summary>
public class ListPortfolioItemsHandlerTests
{
    [Fact]
    public async Task ExecuteAsync_ReturnsOnlyTheCallersOwnItems()
    {
        var callerAccountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        SeedPortfolioItem(dbContext, callerAccountId, "Mine");
        SeedPortfolioItem(dbContext, otherAccountId, "Theirs");

        var request = new ListPortfolioItemsRequest { PageSize = 50 };

        var page = await ListPortfolioItemsHandler.ExecuteAsync(request, dbContext, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal("Mine", item.Label);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsEmptyResult_WhenCallerHasNoItems()
    {
        var callerAccountId = Guid.NewGuid();
        var otherAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);
        SeedPortfolioItem(dbContext, otherAccountId, "Theirs");

        var request = new ListPortfolioItemsRequest { PageSize = 50 };

        var page = await ListPortfolioItemsHandler.ExecuteAsync(request, dbContext, CancellationToken.None);

        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
    }

    [Fact]
    public async Task ExecuteAsync_PaginatesWithSkipTake()
    {
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);

        for (var i = 0; i < 25; i++)
        {
            SeedPortfolioItem(dbContext, callerAccountId, $"Item-{i:D2}");
        }

        var firstPage = await ListPortfolioItemsHandler.ExecuteAsync(
            new ListPortfolioItemsRequest { PageNumber = 1, PageSize = 10 },
            dbContext,
            CancellationToken.None);

        var secondPage = await ListPortfolioItemsHandler.ExecuteAsync(
            new ListPortfolioItemsRequest { PageNumber = 2, PageSize = 10 },
            dbContext,
            CancellationToken.None);

        Assert.Equal(10, firstPage.Items.Count);
        Assert.Equal(10, secondPage.Items.Count);
        Assert.Equal(25, firstPage.TotalCount);
        Assert.DoesNotContain(firstPage.Items, i => secondPage.Items.Any(s => s.Id == i.Id));
    }

    [Fact]
    public async Task ExecuteAsync_UnknownSortKey_Throws()
    {
        var callerAccountId = Guid.NewGuid();
        var (dbContext, _) = CreateDbContext(callerAccountId);
        SeedPortfolioItem(dbContext, callerAccountId, "Item");

        var request = new ListPortfolioItemsRequest { SortBy = "not_a_real_key" };

        await Assert.ThrowsAsync<Storporate.Modules.Portfolio.UnknownSortKeyException>(() =>
            ListPortfolioItemsHandler.ExecuteAsync(request, dbContext, CancellationToken.None));
    }

    private static PortfolioItem SeedPortfolioItem(WriteDbContext dbContext, Guid accountId, string label)
    {
        var item = new PortfolioItem
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            Label = label,
            Category = PortfolioCategories.Document,
            SubmissionType = PortfolioSubmissionTypes.Link,
            ExternalUrl = "https://example.com/x",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        dbContext.PortfolioItems.Add(item);
        dbContext.SaveChanges();
        return item;
    }

    private static (WriteDbContext DbContext, AmbientAccountContext AccountContext) CreateDbContext(Guid callerAccountId)
    {
        var options = new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        var accountContext = new AmbientAccountContext();
        accountContext.SetAccountId(callerAccountId);

        return (new WriteDbContext(options, accountContext), accountContext);
    }
}