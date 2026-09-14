using Microsoft.EntityFrameworkCore;
using Storporate.Infrastructure.Persistence;
using Storporate.Modules.SecurityGovernance;
using Storporate.SharedKernel.Authorization;
using Storporate.SharedKernel.Entities;

namespace Storporate.Tests.Unit.SecurityGovernance;

/// <summary>
/// Unit coverage for <see cref="PermissionService"/>'s actor-type → grant-set lookup. The
/// service's only DB read is "find the user by id and read <see cref="User.ActorType"/>", so
/// the meaningful test surface is: seeded ActorType in the in-memory DB drives which of
/// <see cref="SystemRoles.Grants"/> is consulted. Permission-set membership / non-membership
/// is the assertion.
/// </summary>
public class PermissionServiceTests
{
    [Fact]
    public async Task HasPermissionAsync_ReturnsTrue_WhenActorTypeGrantIncludesThePermission()
    {
        await using var dbContext = CreateDbContext();
        var student = SeedUser(dbContext, ActorTypes.Student);
        var service = new PermissionService(dbContext);

        // Permissions.Jobs.Read is part of SystemRoles.Grants["Student"] (read-only Job
        // browsing), so a Student asking for it must get true.
        var granted = await service.HasPermissionAsync(
            student.Id,
            accountId: student.Id,
            Permissions.Jobs.Read,
            CancellationToken.None);

        Assert.True(granted);
    }

    [Fact]
    public async Task HasPermissionAsync_ReturnsFalse_WhenActorTypeGrantDoesNotIncludeThePermission()
    {
        await using var dbContext = CreateDbContext();
        var student = SeedUser(dbContext, ActorTypes.Student);
        var service = new PermissionService(dbContext);

        // Permissions.Jobs.Write is NOT in SystemRoles.Grants["Student"] — students are
        // read-only on Jobs for now. A Student asking for Write must get false.
        var granted = await service.HasPermissionAsync(
            student.Id,
            accountId: student.Id,
            Permissions.Jobs.Write,
            CancellationToken.None);

        Assert.False(granted);
    }

    [Fact]
    public async Task HasPermissionAsync_OrganizationCanWriteJobs()
    {
        // Positive case mirroring the negative one above — Organization is the role
        // whose grant set includes Jobs.Write by design.
        await using var dbContext = CreateDbContext();
        var organization = SeedUser(dbContext, ActorTypes.Organization);
        var service = new PermissionService(dbContext);

        var granted = await service.HasPermissionAsync(
            organization.Id,
            accountId: organization.Id,
            Permissions.Jobs.Write,
            CancellationToken.None);

        Assert.True(granted);
    }

    [Fact]
    public async Task HasPermissionAsync_AdministratorIsGrantedEveryPermissionInAll()
    {
        await using var dbContext = CreateDbContext();
        var admin = SeedUser(dbContext, ActorTypes.Administrator);
        var service = new PermissionService(dbContext);

        // Walk Permissions.All (rather than hard-coding a literal) so this test stays
        // green as new permissions land — exactly the property SystemRoles.Grants["Administrator"]
        // promises to satisfy.
        foreach (var permission in Permissions.All)
        {
            var granted = await service.HasPermissionAsync(
                admin.Id,
                accountId: admin.Id,
                permission,
                CancellationToken.None);
            Assert.True(granted, $"Administrator must be granted '{permission}'.");
        }
    }

    [Fact]
    public async Task HasPermissionAsync_ReturnsFalse_WhenUserDoesNotExist()
    {
        await using var dbContext = CreateDbContext();
        var service = new PermissionService(dbContext);

        // No user seeded at this id — the service must fail closed (deny), not throw.
        var granted = await service.HasPermissionAsync(
            userId: Guid.NewGuid(),
            accountId: Guid.NewGuid(),
            Permissions.Jobs.Read,
            CancellationToken.None);

        Assert.False(granted);
    }

    [Fact]
    public async Task GetPermissionsAsync_ReturnsTheActorTypesGrantSet()
    {
        await using var dbContext = CreateDbContext();
        var organization = SeedUser(dbContext, ActorTypes.Organization);
        var service = new PermissionService(dbContext);

        var granted = await service.GetPermissionsAsync(
            organization.Id,
            accountId: organization.Id,
            CancellationToken.None);

        Assert.Equal(SystemRoles.Grants[ActorTypes.Organization], granted);
    }

    private static User SeedUser(WriteDbContext dbContext, string actorType)
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            Email = $"{actorType.ToLowerInvariant()}-{Guid.NewGuid():N}@example.com",
            ActorType = actorType,
            VerificationStatus = VerificationStatuses.Verified,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        dbContext.Users.Add(user);
        dbContext.SaveChanges();
        return user;
    }

    private static WriteDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<WriteDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
}
