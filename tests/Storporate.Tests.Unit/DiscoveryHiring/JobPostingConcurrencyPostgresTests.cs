using Microsoft.EntityFrameworkCore;
using Npgsql;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Xunit;

namespace Storporate.Tests.Unit.DiscoveryHiring;

/// <summary>
/// STOR-66 Phase 1: live Postgres proof that the <c>xmin</c> concurrency token
/// on <c>JobPostings</c> actually drives a <c>WHERE xmin = @p_xmin</c> clause
/// against Npgsql, so a stale read followed by a write surfaces
/// <c>DbUpdateConcurrencyException</c> and the handler maps it to
/// <c>JobPostingConflictException</c> → HTTP 409 <c>job_posting_conflict</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>One env-var gate.</b> <c>STORPORATE_TEST_PG</c> is the connection
/// string for the migration role. We don't need the app role here — this
/// proof runs as the migration role so we can directly inspect / bump
/// <c>xmin</c> via raw SQL.
/// </para>
/// <para>
/// <b>What this file does NOT cover.</b> The exception-mapping arm
/// (DbUpdateConcurrencyException → JobPostingConflictException → 409) is
/// covered by the in-memory test in
/// <see cref="JobPostingConflictInMemoryTests"/>; this file only proves
/// the production Npgsql provider + EF Core's xmin mapping actually
/// emit the WHERE clause that causes the conflict.
/// </para>
/// </remarks>
[Collection("LivePg")]
public class JobPostingConcurrencyPostgresTests
{
    private const string MigrationConnectionEnvVar = "STORPORATE_TEST_PG";

    [LivePgFact]
    public async Task StaleXmin_OnUpdate_RaisesDbUpdateConcurrencyException()
    {
        var connectionString = RequireMigrationConnection();

        // 1. Seed a fresh posting under a fresh account (FK requirement).
        var ownerId = Guid.NewGuid();
        var postingId = Guid.NewGuid();
        var staleXmin = await SeedPostingAsync(connectionString, ownerId, postingId);

        try
        {
            // 2. Bump xmin on the row to simulate a concurrent writer committing
            //    AFTER the test loaded the row. The update issued through EF
            //    below will carry the now-stale @p_xmin parameter and the
            //    optimistic-concurrency WHERE clause will return zero rows.
            await BumpXminAsync(connectionString, postingId);

            // 3. Construct a fresh DbContext pointed at the live database,
            //    load the row, mutate one column, and save. The InMemory
            //    provider used by the unit tests can't drive this path, so
            //    we point the Npgsql provider directly at the test DB.
            var options = new DbContextOptionsBuilder<WriteDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            var accountContext = new NullAccountContext();

            using var dbContext = new WriteDbContext(options, accountContext);
            var loaded = await dbContext.JobPostings
                .FirstAsync(p => p.Id == postingId);
            loaded.Title = "Stale Write";

            // The Npgsql provider will execute UPDATE JobPostings SET ...,
            // Title=@p_new, xmin=DEFAULT WHERE Id=@p_id AND xmin=@p_xmin —
            // and the WHERE will match zero rows, surfacing
            // DbUpdateConcurrencyException.
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => dbContext.SaveChangesAsync());
        }
        finally
        {
            await CleanupAsync(connectionString, postingId, ownerId);
        }
    }

    // ----- infrastructure -----

    private static async Task<uint> SeedPostingAsync(string connectionString, Guid ownerId, Guid postingId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        // Foreign-key requirement: JobPostings.OwnerAccountId references Users.Id.
        await using (var insertUser = new NpgsqlCommand(@"
            INSERT INTO ""Users""
                (""Id"", ""Email"", ""ActorType"", ""VerificationStatus"", ""CreatedAt"", ""UpdatedAt"")
            VALUES (@id, @email, 'Organization', 'Verified',
                    now() at time zone 'utc', now() at time zone 'utc')", connection))
        {
            insertUser.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = ownerId });
            insertUser.Parameters.Add(new NpgsqlParameter("@email", NpgsqlTypes.NpgsqlDbType.Text)
            {
                Value = $"live-concurrency-{Guid.NewGuid():N}@example.com"
            });
            await insertUser.ExecuteNonQueryAsync();
        }

        await using (var insertPosting = new NpgsqlCommand(@"
            INSERT INTO ""JobPostings""
                (""Id"", ""OwnerAccountId"", ""Title"", ""Kind"", ""CompanyName"", ""WorkMode"",
                 ""Description"", ""RequiredSkillsJson"", ""Status"", ""Openings"",
                 ""ShowCompensation"", ""SearchText"", ""CreatedAt"", ""UpdatedAt"")
            VALUES (@id, @ownerId, 'Concurrency Role', 'Job', 'LiveCo', 'Hybrid',
                    'Live concurrency fixture.', '[""Python""]'::jsonb, 'Open', 1,
                    false, 'concurrency role liveco python',
                    now() at time zone 'utc', now() at time zone 'utc')
            RETURNING xmin", connection))
        {
            insertPosting.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = postingId });
            insertPosting.Parameters.Add(new NpgsqlParameter("@ownerId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = ownerId });
            var xmin = (uint)(await insertPosting.ExecuteScalarAsync())!;
            return xmin;
        }
    }

    private static async Task BumpXminAsync(string connectionString, Guid postingId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        // A no-op UPDATE forces Postgres to assign a fresh xmin.
        await using var bump = new NpgsqlCommand(
            "UPDATE \"JobPostings\" SET \"UpdatedAt\" = now() at time zone 'utc' WHERE \"Id\" = @id",
            connection);
        bump.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = postingId });
        await bump.ExecuteNonQueryAsync();
    }

    private static async Task CleanupAsync(string connectionString, Guid postingId, Guid ownerId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        try
        {
            await using var delPosting = new NpgsqlCommand(
                "DELETE FROM \"JobPostings\" WHERE \"Id\" = @id", connection);
            delPosting.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = postingId });
            await delPosting.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best effort.
        }

        try
        {
            await using var delUser = new NpgsqlCommand(
                "DELETE FROM \"Users\" WHERE \"Id\" = @id", connection);
            delUser.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = ownerId });
            await delUser.ExecuteNonQueryAsync();
        }
        catch
        {
            // Best effort.
        }
    }

    private static string RequireMigrationConnection()
    {
        var value = Environment.GetEnvironmentVariable(MigrationConnectionEnvVar);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"{MigrationConnectionEnvVar} must be set to an Npgsql connection string for the migration role.");
        }
        return value;
    }
}

/// <summary>
/// Read-only <see cref="IAccountContext"/> for tests that build a
/// <c>WriteDbContext</c> outside the request pipeline (where no ambient
/// account context exists). Every property returns null / false.
/// </summary>
internal sealed class NullAccountContext : IAccountContext
{
    public Guid? UserId => null;
    public Guid? AccountId => null;
    public bool IsAdministrator => false;
    public string? IpAddress => null;
    public string? UserAgent => null;
}

/// <summary>
/// xUnit <see cref="FactAttribute"/> that sets <see cref="FactAttribute.Skip"/>
/// when <c>STORPORATE_TEST_PG</c> is unset, so CI hosts without a reachable
/// Postgres stay green and the assertion only fires against the throwaway
/// container.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class LivePgFactAttribute : FactAttribute
{
    public LivePgFactAttribute()
    {
        var migration = Environment.GetEnvironmentVariable("STORPORATE_TEST_PG");
        if (string.IsNullOrWhiteSpace(migration))
        {
            Skip = "STORPORATE_TEST_PG is not set; this live pg test is gated to opt-in "
                + "runs against the throwaway storporate-pgvector-test container.";
        }
    }
}
