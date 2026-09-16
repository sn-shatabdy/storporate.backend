using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;

namespace Storporate.Tests.Unit.SecurityGovernance.Auditing;

/// <summary>
/// Unit coverage for <see cref="AuditLogWriter"/> under the EF InMemory provider the test
/// suite uses everywhere. The writer's contract under InMemory is "no-op" — it must return
/// without throwing and must not touch any storage. The Postgres-backed hash-chain write
/// path is verified separately by a live test against real local Postgres.
/// </summary>
public class AuditLogWriterTests
{
    [Fact]
    public async Task WriteAsync_UnderInMemoryProvider_ReturnsWithoutThrowing()
    {
        // The provider gate short-circuits the entire Postgres-only path. Under the
        // InMemory provider that the test suite uses, WriteAsync must return cleanly.
        var accountContext = new AmbientAccountContext();
        accountContext.SetUserId(Guid.NewGuid());
        accountContext.SetAccountId(Guid.NewGuid());
        var writer = new AuditLogWriter(
            accountContext,
            Options.Create(new ConnectionStringsOptions
            {
                WriteDb = "Host=unused;Database=unused;Username=unused;Password=unused",
                WriteDbProviderName = "Microsoft.EntityFrameworkCore.InMemory",
            }),
            NullLogger<AuditLogWriter>.Instance);

        // No exception, no observable side effect.
        await writer.WriteAsync(
            action: "test_event",
            resourceType: "Test",
            resourceId: null,
            metadataJson: null,
            cancellationToken: CancellationToken.None);
    }

    [Fact]
    public async Task WriteAsync_UnderInMemoryProvider_DoesNotMutateAmbientContext()
    {
        // Defensive: a swallowed-but-side-effecting writer could mutate the ambient
        // account context for a downstream consumer. The InMemory provider gate must
        // produce a true no-op, not just a swallowed exception.
        var userId = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        var accountContext = new AmbientAccountContext();
        accountContext.SetUserId(userId);
        accountContext.SetAccountId(accountId);
        var writer = new AuditLogWriter(
            accountContext,
            Options.Create(new ConnectionStringsOptions
            {
                WriteDb = "Host=unused;Database=unused;Username=unused;Password=unused",
                WriteDbProviderName = "Microsoft.EntityFrameworkCore.InMemory",
            }),
            NullLogger<AuditLogWriter>.Instance);

        await writer.WriteAsync("a", "b", null, null, CancellationToken.None);

        Assert.Equal(userId, accountContext.UserId);
        Assert.Equal(accountId, accountContext.AccountId);
    }
}
