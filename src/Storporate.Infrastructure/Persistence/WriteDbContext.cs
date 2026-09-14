using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence.Configurations;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence;

/// <summary>
/// The primary write-side EF Core context. Named "WriteDbContext" (rather than a generic
/// "AppDbContext") to leave room for a dedicated read-side context later without a rename.
/// </summary>
public sealed class WriteDbContext(DbContextOptions<WriteDbContext> options) : DbContext(options)
{
    public DbSet<Job> Jobs => Set<Job>();

    public DbSet<User> Users => Set<User>();

    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();

    public DbSet<Session> Sessions => Set<Session>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // All entity mappings live as standalone IEntityTypeConfiguration classes
        // (see Persistence/Configurations/), applied explicitly here.
        modelBuilder.ApplyConfiguration(new UserConfiguration());
        modelBuilder.ApplyConfiguration(new OtpCodeConfiguration());
        modelBuilder.ApplyConfiguration(new SessionConfiguration());
        modelBuilder.ApplyConfiguration(new JobConfiguration());
    }
}