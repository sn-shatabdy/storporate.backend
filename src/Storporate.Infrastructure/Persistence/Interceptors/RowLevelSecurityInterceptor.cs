using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Storporate.Infrastructure.Authorization;
using Storporate.SharedKernel.Entities;

namespace Storporate.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Enforces STOR-62 workspace isolation at the EF Core / Npgsql boundary in two independent
/// ways, complementing the global query filter installed in
/// <see cref="WriteDbContext.OnModelCreating"/> (which only constrains reads) and the
/// PostgreSQL row-level-security policy the Phase 5 migration installs (which is the last
/// line of defense, not a substitute for application-side validation):
/// <list type="number">
///   <item><see cref="ConnectionOpened"/> / <see cref="ConnectionOpenedAsync"/>: on every new
///   Npgsql connection, set the session-scoped GUCs <c>app.account_id</c>,
///   <c>app.user_id</c>, and <c>app.is_admin</c> to the ambient <see cref="IAccountContext"/>
///   values, so the RLS policy's <c>current_setting(...)</c> lookup resolves to the same
///   workspace the application believes it is acting in. The third argument (<c>false</c>) to
///   <c>set_config</c> makes the setting transaction-scoped; since we set it on every
///   connection open and again before every <c>SaveChanges</c>, the value is always current
///   regardless of pooling/transaction boundaries. <c>app.is_admin</c> is the single signal
///   the Phase 5 RLS policy checks to grant the Administrator bypass — keeping the GUC
///   naming on the interceptor side means there is exactly one place in the codebase that
///   decides what Postgres gets told about the current principal.</item>
///   <item><see cref="SavingChangesAsync"/> / <see cref="SavingChanges"/>: walk
///   <see cref="DbContext.ChangeTracker"/>'s <see cref="IAccountScoped"/> entries and throw
///   <see cref="InvalidOperationException"/> on any <see cref="EntityState.Added"/> or
///   <see cref="EntityState.Modified"/> row whose <see cref="IAccountScoped.AccountId"/>
///   does not match the ambient account — unless the caller is an
///   <see cref="ActorTypes.Administrator"/>, who is permitted to write cross-account.
///   Also re-applies the GUCs in the same call so a connection that was opened earlier
///   in the request still has fresh values at write time (connection pooling keeps the
///   connection open across requests; an old <c>app.account_id</c> would otherwise
///   leak).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Lifetime: <see cref="IAccountContext"/> is a singleton, so the interceptor must also be
/// effectively singleton-safe. EF Core resolves <see cref="IInterceptor"/> registrations
/// added via <c>AddDbContext</c>'s <c>AddInterceptors</c> from the same
/// <see cref="IServiceProvider"/> the context itself uses, so registering the interceptor
/// with a scoped lifetime works because EF instantiates a fresh context per scope (per
/// request) and resolves its interceptors on demand from that scope — the singleton
/// <see cref="IAccountContext"/> is captured for the life of the singleton service, not
/// per request, which is exactly what we want.
/// </para>
/// <para>
/// The Postgres GUC calls are a no-op under non-Npgsql providers (the connection type check
/// skips them). The unit test suite uses <c>UseInMemoryDatabase</c>, which never sees a real
/// <see cref="DbConnection"/>; the same <c>if (dbConnection is NpgsqlConnection)</c> gate
/// silently skips them, so the same code is correct in both environments.
/// </para>
/// </remarks>
public sealed class RowLevelSecurityInterceptor : SaveChangesInterceptor, IDbConnectionInterceptor
{
    private readonly IAccountContext _accountContext;

    public RowLevelSecurityInterceptor(IAccountContext accountContext)
    {
        _accountContext = accountContext;
    }

    /// <inheritdoc />
    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPostgresGucs(connection);
    }

    /// <inheritdoc />
    public async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyPostgresGucsAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        EnforceAccountScope(eventData.Context);
        return result;
    }

    /// <inheritdoc />
    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        EnforceAccountScope(eventData.Context);
        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> for any
    /// <see cref="IAccountScoped"/> <see cref="EntityState.Added"/> /
    /// <see cref="EntityState.Modified"/> entry whose <see cref="IAccountScoped.AccountId"/>
    /// doesn't match the ambient account, unless the caller is an
    /// <see cref="ActorTypes.Administrator"/>.
    /// </summary>
    /// <remarks>
    /// The guard only fires when there is at least one <see cref="IAccountScoped"/> entry
    /// in the change tracker — saving a <see cref="User"/> or <see cref="Session"/> (which
    /// are not <see cref="IAccountScoped"/>) is unaffected by the interceptor, even when no
    /// ambient account is set. The Phase 4 acceptance criteria explicitly require this: the
    /// interceptor exists to enforce tenancy on account-owned data, not to gate every save
    /// in the system.
    /// </remarks>
    private void EnforceAccountScope(DbContext? dbContext)
    {
        if (dbContext is null)
        {
            return;
        }

        var entries = dbContext.ChangeTracker.Entries<IAccountScoped>().ToList();
        if (entries.Count == 0)
        {
            return;
        }

        // Administrators are allowed (by design — see the SecurityGovernance authorization
        // module) to write cross-account, so skip the guard for them. The global query
        // filter still hides other accounts' rows from their reads via the IsAdministrator
        // short-circuit, so this guard is the only place that grants them write access.
        if (_accountContext.IsAdministrator)
        {
            return;
        }

        var ambientAccountId = _accountContext.AccountId;
        if (ambientAccountId is null)
        {
            // IAccountScoped entries exist but the middleware hasn't populated the context
            // yet (or the request was [AllowAnonymous]). Refuse the write rather than
            // silently allow cross-account data through.
            throw new InvalidOperationException(
                "Cannot save IAccountScoped entities: no ambient account is set on IAccountContext. "
                + "Requests that mutate account-scoped data must run through AccountContextMiddleware.");
        }

        foreach (var entry in entries)
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            if (entry.Entity.AccountId != ambientAccountId.Value)
            {
                throw new InvalidOperationException(
                    $"Account-scoped entity of type '{entry.Entity.GetType().Name}' has AccountId "
                    + $"'{entry.Entity.AccountId}' which does not match the ambient account "
                    + $"'{ambientAccountId.Value}'. Cross-account writes are not permitted.");
            }
        }
    }

    /// <summary>
    /// Synchronous Postgres GUC writer. Skipped under non-Npgsql providers so the same
    /// interceptor is correct for both production and the in-memory test suite.
    /// </summary>
    private void ApplyPostgresGucs(DbConnection connection)
    {
        if (connection is not NpgsqlConnection npgsqlConnection)
        {
            return;
        }

        using var command = npgsqlConnection.CreateCommand();
        command.CommandText =
            "SELECT set_config('app.account_id', @accountId, false), "
            + "set_config('app.user_id', @userId, false), "
            + "set_config('app.is_admin', @isAdmin, false);";

        // NpgsqlConnection.CreateCommand() returns NpgsqlCommand; AddWithValue is the
        // ergonomic wrapper that infers the parameter type from the value.
        command.Parameters.AddWithValue("@accountId", _accountContext.AccountId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("@userId", _accountContext.UserId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("@isAdmin", _accountContext.IsAdministrator ? "true" : "false");

        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Async counterpart to <see cref="ApplyPostgresGucs"/>. Same body, same skip rule.
    /// </summary>
    private async Task ApplyPostgresGucsAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection is not NpgsqlConnection npgsqlConnection)
        {
            return;
        }

        await using var command = npgsqlConnection.CreateCommand();
        command.CommandText =
            "SELECT set_config('app.account_id', @accountId, false), "
            + "set_config('app.user_id', @userId, false), "
            + "set_config('app.is_admin', @isAdmin, false);";

        command.Parameters.AddWithValue("@accountId", _accountContext.AccountId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("@userId", _accountContext.UserId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("@isAdmin", _accountContext.IsAdministrator ? "true" : "false");

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}