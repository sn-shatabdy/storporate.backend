namespace Storporate.SharedKernel.Entities;

/// <summary>
/// Marker interface for entities that belong to a single account (one
/// <see cref="User"/> acting in the role of one of the four self-service
/// <see cref="ActorTypes"/>: <see cref="ActorTypes.Student"/>,
/// <see cref="ActorTypes.Organization"/>, <see cref="ActorTypes.University"/>,
/// <see cref="ActorTypes.Club"/>). Because no multi-user-per-account / team-membership
/// concept exists or is being introduced (see the STOR-62 plan, Context &amp; Findings —
/// "Workspace = account"), the tenancy column references <see cref="User.Id"/> directly —
/// no separate <c>Account</c>/<c>WorkspaceMembers</c> join table is needed.
/// </summary>
/// <remarks>
/// Implementing this interface opts an entity into STOR-62's three-layer isolation
/// pipeline:
/// <list type="number">
///   <item>The EF Core global query filter installed in <c>WriteDbContext.OnModelCreating</c>
///   (Phase 4) restricts reads to the ambient account.</item>
///   <item>The save-time <c>RowLevelSecurityInterceptor</c> (Phase 4) validates that any
///   <see cref="Microsoft.EntityFrameworkCore.EntityState.Added"/> or
///   <see cref="Microsoft.EntityFrameworkCore.EntityState.Modified"/> row's
///   <see cref="AccountId"/> matches the ambient account.</item>
///   <item>The PostgreSQL row-level security policy added by the Phase 5 migration enforces
///   the same isolation at the database layer, independent of the application.</item>
/// </list>
/// <see cref="ActorTypes.Administrator"/> contexts deliberately bypass all three layers via
/// the SecurityGovernance authorization module; the interceptor's guard and the RLS policy
/// both special-case the Administrator path rather than skipping it at the application layer.
/// </remarks>
public interface IAccountScoped
{
    /// <summary>The id of the <see cref="User"/> that owns this row. Foreign-keyed to
    /// <c>Users.Id</c> with <see cref="Microsoft.EntityFrameworkCore.DeleteBehavior.Restrict"/>
    /// so a user cannot be deleted while they still own account-scoped data.</summary>
    Guid AccountId { get; }
}