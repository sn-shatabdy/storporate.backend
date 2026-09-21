using Microsoft.EntityFrameworkCore;
using Npgsql;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Xunit;

namespace Storporate.Tests.Unit.DiscoveryHiring;

/// <summary>
/// STOR-67 Phase 1: live Postgres proof that the <c>xmin</c> concurrency token
/// on <c>JobApplications</c> actually drives a <c>WHERE xmin = @p_xmin</c> clause
/// against Npgsql, so a stale read followed by a write surfaces
/// <c>DbUpdateConcurrencyException</c> and the handler maps it to
/// <c>ApplicationConflictException</c> → HTTP 409 <c>application_conflict</c>.
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
/// (DbUpdateConcurrencyException → ApplicationConflictException → 409) is
/// covered by the in-memory test in
/// <see cref="ApplicationConflictInMemoryTests"/>; this file only proves
/// the production Npgsql provider + EF Core's xmin mapping actually
/// emit the WHERE clause that causes the conflict.
/// </para>
/// </remarks>
[Collection("LivePg")]
public class JobApplicationConcurrencyPostgresTests
{
    private const string MigrationConnectionEnvVar = "STORPORATE_TEST_PG";

    [LivePgFact]
    public async Task StaleXmin_OnUpdate_RaisesDbUpdateConcurrencyException()
    {
        var connectionString = RequireMigrationConnection();

        // 1. Seed a fresh account + posting + application under it (FK requirement).
        var ownerId = Guid.NewGuid();
        var studentId = Guid.NewGuid();
        var postingId = Guid.NewGuid();
        var applicationId = Guid.NewGuid();
        await SeedApplicationAsync(
            connectionString, ownerId, studentId, postingId, applicationId);

        // Construct a fresh DbContext pointed at the live database. The InMemory
        // provider used by the unit tests can't drive this path, so we point
        // the Npgsql provider directly at the test DB.
        var options = new DbContextOptionsBuilder<WriteDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var accountContext = new NullAccountContext();

        using var dbContext = new WriteDbContext(options, accountContext);

        try
        {
            // 2. Load the row FIRST, capturing the xmin EF tracked.
            var loaded = await dbContext.JobApplications
                .FirstAsync(a => a.Id == applicationId);

            // 3. Bump xmin on the row to simulate a concurrent writer
            //    committing AFTER the test loaded the row. The update issued
            //    through EF below will carry the now-stale @p_xmin parameter
            //    and the optimistic-concurrency WHERE clause will return zero
            //    rows.
            await BumpXminAsync(connectionString, applicationId);

            loaded.Status = "Viewed";

            // The Npgsql provider will execute UPDATE JobApplications SET ...,
            // Status=@p_new, xmin=DEFAULT WHERE Id=@p_id AND xmin=@p_xmin —
            // and the WHERE will match zero rows, surfacing
            // DbUpdateConcurrencyException.
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => dbContext.SaveChangesAsync());
        }
        finally
        {
            await CleanupAsync(connectionString, applicationId, postingId, studentId, ownerId);
        }
    }

    // ----- infrastructure -----

    private static async Task<uint> SeedApplicationAsync(
        string connectionString,
        Guid ownerId,
        Guid studentId,
        Guid postingId,
        Guid applicationId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        // Foreign-key requirement: JobApplications.StudentAccountId references Users.Id.
        // JobApplications.JobPostingId references JobPostings.Id, which references Users.Id.
        await using (var insertOwner = new NpgsqlCommand(@"
            INSERT INTO ""Users""
                (""Id"", ""Email"", ""ActorType"", ""VerificationStatus"", ""CreatedAt"", ""UpdatedAt"")
            VALUES (@id, @email, 'Organization', 'Verified',
                    now() at time zone 'utc', now() at time zone 'utc')", connection))
        {
            insertOwner.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = ownerId });
            insertOwner.Parameters.Add(new NpgsqlParameter("@email", NpgsqlTypes.NpgsqlDbType.Text)
            {
                Value = $"live-app-owner-{Guid.NewGuid():N}@example.com"
            });
            await insertOwner.ExecuteNonQueryAsync();
        }

        await using (var insertStudent = new NpgsqlCommand(@"
            INSERT INTO ""Users""
                (""Id"", ""Email"", ""ActorType"", ""VerificationStatus"", ""CreatedAt"", ""UpdatedAt"")
            VALUES (@id, @email, 'Student', 'Verified',
                    now() at time zone 'utc', now() at time zone 'utc')", connection))
        {
            insertStudent.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = studentId });
            insertStudent.Parameters.Add(new NpgsqlParameter("@email", NpgsqlTypes.NpgsqlDbType.Text)
            {
                Value = $"live-app-student-{Guid.NewGuid():N}@example.com"
            });
            await insertStudent.ExecuteNonQueryAsync();
        }

        await using (var insertPosting = new NpgsqlCommand(@"
            INSERT INTO ""JobPostings""
                (""Id"", ""OwnerAccountId"", ""Title"", ""Kind"", ""CompanyName"", ""WorkMode"",
                 ""Description"", ""RequiredSkills"", ""Status"", ""Openings"",
                 ""ShowCompensation"", ""SearchText"", ""CreatedAt"", ""UpdatedAt"")
            VALUES (@id, @ownerId, 'App Concurrency Role', 'Job', 'LiveCo', 'Hybrid',
                    'Live concurrency fixture.', '[""Python""]'::text, 'Open', 1,
                    false, 'app concurrency role liveco python',
                    now() at time zone 'utc', now() at time zone 'utc')", connection))
        {
            insertPosting.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = postingId });
            insertPosting.Parameters.Add(new NpgsqlParameter("@ownerId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = ownerId });
            await insertPosting.ExecuteNonQueryAsync();
        }

        await using (var insertApplication = new NpgsqlCommand(@"
            INSERT INTO ""JobApplications""
                (""Id"", ""JobPostingId"", ""StudentAccountId"", ""Status"",
                 ""SnapshotJson"", ""CreatedAt"", ""UpdatedAt"", ""StatusChangedAt"")
            VALUES (@id, @postingId, @studentId, 'Submitted',
                    '{}'::text, now() at time zone 'utc',
                    now() at time zone 'utc', now() at time zone 'utc')
            RETURNING xmin", connection))
        {
            insertApplication.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = applicationId });
            insertApplication.Parameters.Add(new NpgsqlParameter("@postingId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = postingId });
            insertApplication.Parameters.Add(new NpgsqlParameter("@studentId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = studentId });
            var xmin = (uint)(await insertApplication.ExecuteScalarAsync())!;
            return xmin;
        }
    }

    private static async Task BumpXminAsync(string connectionString, Guid applicationId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        // A no-op UPDATE forces Postgres to assign a fresh xmin.
        await using var bump = new NpgsqlCommand(
            "UPDATE \"JobApplications\" SET \"UpdatedAt\" = now() at time zone 'utc' WHERE \"Id\" = @id",
            connection);
        bump.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = applicationId });
        await bump.ExecuteNonQueryAsync();
    }

    private static async Task CleanupAsync(
        string connectionString,
        Guid applicationId,
        Guid postingId,
        Guid studentId,
        Guid ownerId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        foreach (var (table, id) in new (string Table, Guid Id)[]
                 {
            ("JobApplications", applicationId),
            ("JobPostings", postingId),
            ("Users", studentId),
            ("Users", ownerId),
                 })
        {
            try
            {
                await using var del = new NpgsqlCommand(
                    $"DELETE FROM \"{table}\" WHERE \"Id\" = @id", connection);
                del.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = id });
                await del.ExecuteNonQueryAsync();
            }
            catch
            {
                // Best effort.
            }
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
