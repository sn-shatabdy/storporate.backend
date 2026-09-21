using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Storporate.SharedKernel;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.SharedKernel;

/// <summary>
/// STOR-43 Phase 1: pin the contract of <see cref="TalentIndexRefreshJobs.EnqueueIfSearchableAsync"/>.
/// Three call sites depend on it (<c>PortfolioAnalysisJobProcessor</c> after a successful analysis,
/// <c>DeletePortfolioItemHandler</c> after a delete, and <c>UpdateSearchableProfileHandler</c> on a
/// false-to-true toggle) so the gate behavior — "enqueue only when the student is opted in" —
/// must be enforced in one place. These tests run against the in-memory EF provider because the
/// helper takes <see cref="DbContext"/> (typed away from <see cref="WriteDbContext"/> to avoid
/// a SharedKernel → Infrastructure reference) and operates purely on the change tracker.
/// </summary>
public class TalentIndexRefreshJobsTests
{
    [Fact]
    public async Task EnqueueIfSearchableAsync_NonSearchableStudent_ReturnsEmptyAndAddsNoJob()
    {
        var (dbContext, accountId) = CreateInMemoryContext();
        SeedProfile(dbContext, accountId, isSearchable: false);

        var jobId = await TalentIndexRefreshJobs.EnqueueIfSearchableAsync(
            dbContext, accountId, DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(Guid.Empty, jobId);
        Assert.DoesNotContain(
            dbContext.ChangeTracker.Entries<Job>(),
            e => e.State == EntityState.Added);
    }

    [Fact]
    public async Task EnqueueIfSearchableAsync_StudentWithoutProfileRow_ReturnsEmptyAndAddsNoJob()
    {
        // A student who never toggled the feature has no StudentSearchProfile row.
        // The helper's AsNoTracking read returns null, the IsSearchable guard short-
        // circuits, and no Job row is added. Equivalent to "opted out" for the
        // gate's purposes.
        var (dbContext, accountId) = CreateInMemoryContext();

        var jobId = await TalentIndexRefreshJobs.EnqueueIfSearchableAsync(
            dbContext, accountId, DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(Guid.Empty, jobId);
        Assert.DoesNotContain(
            dbContext.ChangeTracker.Entries<Job>(),
            e => e.State == EntityState.Added);
    }

    [Fact]
    public async Task EnqueueIfSearchableAsync_SearchableStudent_AddsPendingJobWithCorrectPayload()
    {
        var (dbContext, accountId) = CreateInMemoryContext();
        SeedProfile(dbContext, accountId, isSearchable: true);

        var now = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        var jobId = await TalentIndexRefreshJobs.EnqueueIfSearchableAsync(
            dbContext, accountId, now, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, jobId);

        var addedEntry = Assert.Single(
            dbContext.ChangeTracker.Entries<Job>(),
            e => e.State == EntityState.Added);
        var addedJob = addedEntry.Entity;
        Assert.Equal(jobId, addedJob.Id);
        Assert.Equal(TalentIndexJobTypes.RefreshEntry, addedJob.Type);
        Assert.Equal(JobStatus.Pending, addedJob.Status);
        Assert.Equal(0, addedJob.AttemptCount);
        Assert.Equal(accountId, addedJob.AccountId);
        Assert.Equal(now, addedJob.CreatedAt);
        Assert.Equal(now, addedJob.UpdatedAt);

        var payload = JsonSerializer.Deserialize<RefreshTalentIndexPayload>(addedJob.PayloadJson);
        Assert.NotNull(payload);
        Assert.Equal(accountId, payload!.StudentAccountId);
    }

    [Fact]
    public async Task EnqueueIfSearchableAsync_DoesNotCallSaveChangesAsync()
    {
        // The helper adds the job to the change tracker but must NOT call
        // SaveChangesAsync — the caller's pending SaveChangesAsync commits it
        // atomically alongside whatever other write the call site was already
        // making. If the helper committed independently the audit row would
        // land in one commit and the job row in another, reintroducing the
        // "portfolio row updated but no refresh job enqueued" gap.
        var (dbContext, accountId) = CreateInMemoryContext();
        SeedProfile(dbContext, accountId, isSearchable: true);

        await TalentIndexRefreshJobs.EnqueueIfSearchableAsync(
            dbContext, accountId, DateTime.UtcNow, CancellationToken.None);

        // The job is Added in the change tracker; without a SaveChanges the
        // store sees no rows. Caller's job to commit.
        Assert.Single(
            dbContext.ChangeTracker.Entries<Job>(),
            e => e.State == EntityState.Added);
        Assert.Empty(await dbContext.Jobs.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task EnqueueIfSearchableAsync_EmptyStudentAccountId_ReturnsEmpty()
    {
        // Defensive guard against the call site passing Guid.Empty — the helper
        // refuses the enqueue and lets the processor-side
        // SystemAccountScope work fall through to a no-op.
        var (dbContext, _) = CreateInMemoryContext();

        var jobId = await TalentIndexRefreshJobs.EnqueueIfSearchableAsync(
            dbContext, Guid.Empty, DateTime.UtcNow, CancellationToken.None);

        Assert.Equal(Guid.Empty, jobId);
        Assert.DoesNotContain(
            dbContext.ChangeTracker.Entries<Job>(),
            e => e.State == EntityState.Added);
    }

    // ----- infrastructure -----

    private static (WriteDbContext DbContext, Guid AccountId) CreateInMemoryContext()
    {
        var accountId = Guid.NewGuid();
        var accountContext = new AmbientAccountContext();
        accountContext.SetAccountId(accountId);
        var options = new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase("TalentIndexRefreshJobsTests-" + Guid.NewGuid().ToString("N"))
            .Options;
        return (new WriteDbContext(options, accountContext), accountId);
    }

    private static void SeedProfile(WriteDbContext dbContext, Guid accountId, bool isSearchable)
    {
        dbContext.StudentSearchProfiles.Add(new StudentSearchProfile
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            IsSearchable = isSearchable,
            DisplayName = isSearchable ? "Seed Student" : string.Empty,
            OptedInAt = isSearchable ? DateTimeOffset.UtcNow : null,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        dbContext.SaveChanges();
    }
}