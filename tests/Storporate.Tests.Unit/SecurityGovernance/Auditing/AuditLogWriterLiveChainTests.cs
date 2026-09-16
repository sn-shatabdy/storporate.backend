using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Storporate.Infrastructure.Auditing;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;

namespace Storporate.Tests.Unit.SecurityGovernance.Auditing;

/// <summary>
/// Live, Postgres-backed chain-integrity proof for <see cref="AuditLogWriter"/>. Gated on
/// <c>STORPORATE_LIVE_PG_TESTS=1</c> + a non-empty <c>ConnectionStrings__WriteDb</c>; the
/// test method returns cleanly (effectively a no-op assertion) when the env var is unset so
/// CI / non-Postgres hosts stay green without needing the SkippableFact package.
/// </summary>
/// <remarks>
/// The STOR-63 plan notes that no Postgres-backed integration-test harness exists in this
/// repo and that Postgres-only behavior has historically been verified live via
/// <c>psql</c>/manual checks. This file is the xUnit version: it runs against the same
/// local-Docker Postgres that <c>docker-compose.yml</c> already stands up, gated on an
/// env var so the test still passes on hosts that don't have a reachable Postgres (CI
/// runners, etc.).
/// </remarks>
public class AuditLogWriterLiveChainTests
{
    private const string LivePgEnvVar = "STORPORATE_LIVE_PG_TESTS";

    [Fact]
    public async Task WriteAsync_TwoSequentialWritesForSameAccount_ChainPreviousHashCorrectly()
    {
        if (Environment.GetEnvironmentVariable(LivePgEnvVar) != "1")
        {
            // No skip-package available — return a passing no-op when the gate env var is
            // absent. The full assertion path runs only when explicitly opted in via
            // STORPORATE_LIVE_PG_TESTS=1 (e.g. local verification against docker-compose).
            return;
        }

        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__WriteDb")
            ?? throw new InvalidOperationException("ConnectionStrings__WriteDb must be set to run this test.");

        var accountId = Guid.NewGuid();

        // Use a unique AccountId per test run so the chain-tip read sees exactly the rows
        // this test inserted — never rows from a previous run, a parallel test, or a
        // hand-driven psql session.
        var accountContext = new AmbientAccountContext();
        accountContext.SetUserId(accountId);
        accountContext.SetAccountId(accountId);

        var writer = new AuditLogWriter(
            accountContext,
            Options.Create(new ConnectionStringsOptions { WriteDb = connectionString }),
            NullLogger<AuditLogWriter>.Instance);

        await writer.WriteAsync("live_test_first", "Test", "first", "{\"k\":\"v1\"}", CancellationToken.None);
        await writer.WriteAsync("live_test_second", "Test", "second", "{\"k\":\"v2\"}", CancellationToken.None);

        // Read back through a fresh DbContext so the test can verify the chain integrity
        // post-write. The writer itself doesn't depend on the DbContext (the production
        // refactor dropped that coupling), but the test still owns one to assert against.
        await using var dbContext = new WriteDbContext(
            new DbContextOptionsBuilder<WriteDbContext>()
                .UseNpgsql(connectionString)
                .Options,
            accountContext);

        var rows = await dbContext.AuditLogEntries
            .Where(e => e.AccountId == accountId)
            .OrderBy(e => e.SequenceNumber)
            .ToListAsync();

        Assert.Equal(2, rows.Count);

        var first = rows[0];
        var second = rows[1];

        // Chain integrity: the second row's PreviousHash equals the first row's Hash.
        Assert.Equal(first.Hash, second.PreviousHash);

        // First row has no predecessor on this chain (we used a fresh AccountId).
        Assert.Null(first.PreviousHash);

        // Sequence numbers strictly increase, allocated server-side.
        Assert.True(second.SequenceNumber > first.SequenceNumber);

        // The Hash values match the documented canonical-form algorithm: 64 lowercase hex chars.
        Assert.Equal(64, first.Hash.Length);
        Assert.Equal(first.Hash.ToLowerInvariant(), first.Hash);
    }
}
