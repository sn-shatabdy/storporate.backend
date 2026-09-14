namespace Storporate.Infrastructure.Authorization;

/// <summary>
/// <see cref="IAccountContext"/> + <see cref="IAccountContextWriter"/> backed by
/// <see cref="AsyncLocal{T}"/> so the values follow the current async-flow without each
/// consumer needing an explicit dependency on <see cref="Microsoft.AspNetCore.Http.IHttpContextAccessor"/>.
/// </summary>
/// <remarks>
/// <para>
/// Why <see cref="AsyncLocal{T}"/> rather than <see cref="Microsoft.AspNetCore.Http.IHttpContextAccessor"/>?
/// The three consumers in scope today (<c>PermissionAuthorizationHandler</c>, the EF Core
/// global query filter in <c>WriteDbContext.OnModelCreating</c>, the
/// <c>RowLevelSecurityInterceptor</c>) all need to read the ambient account context from
/// places that don't naturally take a <see cref="Microsoft.AspNetCore.Http.HttpContext"/>
/// — EF Core expression filters can't reach one, and the interceptor gets called from EF
/// Core's own pipeline. <see cref="AsyncLocal{T}"/> flows the value through any
/// <c>await</c>/<c>ConfigureAwait</c>/<see cref="System.Threading.Tasks.Task.Run{TResult}(System.Func{System.Threading.Tasks.Task{TResult}?})"/>
/// boundary the host takes, and the middleware's per-request <c>Set*</c> calls happen
/// before any of those consumers run, so the values are reliably visible.
/// </para>
/// <para>
/// Registered as <see cref="Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton"/>:
/// the <see cref="AsyncLocal{T}"/> fields are the storage, and they survive across request
/// scopes by definition, so the singleton holder is the natural lifetime. A scoped or
/// transient registration would create a fresh <see cref="AsyncLocal{T}"/> instance per
/// request and the values written by <see cref="AccountContextMiddleware"/> would never
/// reach a downstream consumer that resolved the service from a different scope.
/// </para>
/// </remarks>
public sealed class AmbientAccountContext : IAccountContext, IAccountContextWriter
{
    // Three independent AsyncLocals rather than one struct-typed one — simpler semantics
    // around "set AccountId but leave UserId alone" (the middleware does exactly this when
    // an {accountId} route value is present but the JWT subject is already validated).
    private readonly AsyncLocal<Guid?> _userId = new();
    private readonly AsyncLocal<Guid?> _accountId = new();
    private readonly AsyncLocal<bool> _isAdministrator = new();

    /// <inheritdoc />
    public Guid? UserId => _userId.Value;

    /// <inheritdoc />
    public Guid? AccountId => _accountId.Value;

    /// <inheritdoc />
    public bool IsAdministrator => _isAdministrator.Value;

    /// <inheritdoc />
    public void SetUserId(Guid? value) => _userId.Value = value;

    /// <inheritdoc />
    public void SetAccountId(Guid? value) => _accountId.Value = value;

    /// <inheritdoc />
    public void SetIsAdministrator(bool value) => _isAdministrator.Value = value;
}
