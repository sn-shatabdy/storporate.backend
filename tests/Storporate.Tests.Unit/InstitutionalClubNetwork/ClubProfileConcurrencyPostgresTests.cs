using Microsoft.EntityFrameworkCore;
using Npgsql;
using Storporate.Infrastructure.Authorization;
using Storporate.Infrastructure.Persistence;
using Xunit;

namespace Storporate.Tests.Unit.InstitutionalClubNetwork;

/// <summary>
/// STOR-69 Phase 1: live Postgres proof that the <c>xmin</c> concurrency token
/// on <c>ClubProfiles</c> actually drives a <c>WHERE xmin = @p_xmin</c> clause
/// against Npgsql, so a stale read followed by a write surfaces
/// <c>DbUpdateConcurrencyException</c> and the handler maps it to
/// <c>ClubProfileConflictException</c> → HTTP 409 <c>club_profile_conflict</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is its own file (not folded into the endpoint tests).</b> The
/// endpoint tests run against the in-memory EF provider, which never reads or
/// emits <c>xmin</c> — so they cannot prove the WHERE clause fires. This file
/// points the Npgsql provider directly at the test Postgres to exercise the
/// real EF Core xmin mapping.
/// </para>
/// <para>
/// <b>One env-var gate.</b> <c>STORPORATE_TEST_PG</c> is the connection
/// string for the migration role. We don't need the app role here — this
/// proof runs as the migration role so we can directly inspect / bump
/// <c>xmin</c> via raw SQL.
/// </para>
/// <para>
/// <b>What this file does NOT cover.</b> The exception-mapping arm
/// (DbUpdateConcurrencyException → ClubProfileConflictException → 409) is
/// covered by the in-memory test in
/// <see cref="ClubProfileConflictInMemoryTests"/>; this file only proves the
/// production Npgsql provider + EF Core's xmin mapping actually emit the
/// WHERE clause that causes the conflict.
/// </para>
/// </remarks>
[Collection("LivePg")]
public class ClubProfileConcurrencyPostgresTests
{
    private const string MigrationConnectionEnvVar = "STORPORATE_TEST_PG";

    [LivePgFact]
    public async Task StaleXmin_OnUpdate_RaisesDbUpdateConcurrencyException()
    {
        var connectionString = RequireMigrationConnection();

        // 1. Seed a fresh profile under a fresh account (FK requirement).
        var ownerId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        await SeedProfileAsync(connectionString, ownerId, profileId);

        try
        {
            // Construct a fresh DbContext pointed at the live database. The
            // InMemory provider used by the unit tests can't drive the xmin
            // concurrency-token path, so we point the Npgsql provider
            // directly at the test DB.
            var options = new DbContextOptionsBuilder<WriteDbContext>()
                .UseNpgsql(connectionString)
                .Options;
            var accountContext = new NullAccountContext();

            using var dbContext = new WriteDbContext(options, accountContext);

            // 2. Load FIRST — this is the "reader" whose change tracker will
            //    capture the row's current xmin as the @p_xmin concurrency
            //    token to enforce on save.
            var loaded = await dbContext.ClubProfiles
                .FirstAsync(p => p.Id == profileId);

            // 3. THEN bump xmin out from under it via a raw-SQL no-op,
            //    simulating a concurrent writer committing between this
            //    reader's load and its save. After this the row's xmin in
            //    the database is strictly newer than the @p_xmin EF holds.
            await BumpXminAsync(connectionString, profileId);

            // 4. Mutate and save with the now-stale xmin. The Npgsql
            //    provider executes UPDATE ClubProfiles SET ..., xmin=DEFAULT
            //    WHERE Id=@p_id AND xmin=@p_xmin — and the WHERE matches
            //    zero rows because @p_xmin is stale, surfacing
            //    DbUpdateConcurrencyException.
            loaded.Name = "Stale Write";

            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(
                () => dbContext.SaveChangesAsync());
        }
        finally
        {
            await CleanupAsync(connectionString, profileId, ownerId);
        }
    }

    // ----- infrastructure -----

    private static async Task SeedProfileAsync(string connectionString, Guid ownerId, Guid profileId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        // Foreign-key requirement: ClubProfiles.OwnerAccountId references Users.Id.
        await using (var insertUser = new NpgsqlCommand(@"
            INSERT INTO ""Users""
                (""Id"", ""Email"", ""ActorType"", ""VerificationStatus"", ""CreatedAt"", ""UpdatedAt"")
            VALUES (@id, @email, 'Club', 'Verified',
                    now() at time zone 'utc', now() at time zone 'utc')", connection))
        {
            insertUser.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = ownerId });
            insertUser.Parameters.Add(new NpgsqlParameter("@email", NpgsqlTypes.NpgsqlDbType.Text)
            {
                Value = $"live-concurrency-{Guid.NewGuid():N}@example.com"
            });
            await insertUser.ExecuteNonQueryAsync();
        }

        await using (var insertProfile = new NpgsqlCommand(@"
            INSERT INTO ""ClubProfiles""
                (""Id"", ""OwnerAccountId"", ""Name"", ""About"", ""University"", ""MemberCount"",
                 ""AudienceFieldsOfStudy"", ""AudienceYears"", ""Events"",
                 ""Status"", ""CreatedAt"", ""UpdatedAt"")
            VALUES (@id, @ownerId, 'Concurrency Role', 'Live fixture.', 'Test U', 1,
                    '[]', '[]', '[]', 'Draft',
                    now() at time zone 'utc', now() at time zone 'utc')", connection))
        {
            insertProfile.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = profileId });
            insertProfile.Parameters.Add(new NpgsqlParameter("@ownerId", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = ownerId });
            await insertProfile.ExecuteNonQueryAsync();
        }
    }

    private static async Task BumpXminAsync(string connectionString, Guid profileId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        // A no-op UPDATE forces Postgres to assign a fresh xmin.
        await using var bump = new NpgsqlCommand(
            "UPDATE \"ClubProfiles\" SET \"UpdatedAt\" = now() at time zone 'utc' WHERE \"Id\" = @id",
            connection);
        bump.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = profileId });
        await bump.ExecuteNonQueryAsync();
    }

    private static async Task CleanupAsync(string connectionString, Guid profileId, Guid ownerId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        try
        {
            await using var delProfile = new NpgsqlCommand(
                "DELETE FROM \"ClubProfiles\" WHERE \"Id\" = @id", connection);
            delProfile.Parameters.Add(new NpgsqlParameter("@id", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = profileId });
            await delProfile.ExecuteNonQueryAsync();
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
