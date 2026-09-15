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
///   connection open and again before every command EF Core issues, the value is always
///   current regardless of pooling/transaction boundaries. <c>app.is_admin</c> is the single
///   signal the Phase 5 RLS policy checks to grant the Administrator bypass — keeping the GUC
///   naming on the interceptor side means there is exactly one place in the codebase that
///   decides what Postgres gets told about the current principal.</item>
///   <item><see cref="SavingChangesAsync"/> / <see cref="SavingChanges"/>: walk
///   <see cref="DbContext.ChangeTracker"/>'s <see cref="IAccountScoped"/> entries and throw
///   <see cref="InvalidOperationException"/> on any <see cref="EntityState.Added"/> or
///   <see cref="EntityState.Modified"/> row whose <see cref="IAccountScoped.AccountId"/>
///   does not match the ambient account — unless the caller is an
///   <see cref="ActorTypes.Administrator"/>, who is permitted to write cross-account.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this also implements <see cref="IDbCommandInterceptor"/>:</b>
/// <see cref="ConnectionOpened"/> alone is insufficient to keep Postgres'
/// session-scoped GUCs fresh. Npgsql pools physical connections by default, and a pooled
/// connection handed out for a plain read query (no <c>SaveChanges</c> in flight) still
/// carries the GUCs set the last time somebody wrote something on it. If request N for
/// account A sets <c>app.account_id = A</c>, the pool returns the same physical connection
/// for request N+1 for account B (or an Administrator), and B's read sees A's GUCs — a
/// stale-GUC window in the RLS backstop that no application-side enforcement catches. To
/// close it, every command EF Core issues re-applies the GUCs immediately before the SQL
/// goes on the wire, via <see cref="ReaderExecuting"/>, <see cref="ReaderExecutingAsync"/>,
/// <see cref="ScalarExecuting"/>, <see cref="ScalarExecutingAsync"/>,
/// <see cref="NonQueryExecuting"/>, and <see cref="NonQueryExecutingAsync"/>. EF Core
/// dispatches every SQL command through one of those three execution paths, so these six
/// hooks give full coverage.
/// </para>
/// <para>
/// All six hooks share the same body as <see cref="ConnectionOpened"/> (refresh the GUCs)
/// via the existing <see cref="ApplyPostgresGucs"/> / <see cref="ApplyPostgresGucsAsync"/>
/// helpers — the SQL is built once in <see cref="BuildGucSetCommand"/> and is the same
/// <c>SELECT set_config(...)</c> statement run on every hook. Cost-wise: one extra
/// round-trip per command on Npgsql, amortized against pooled connections that were
/// already going to be reused.
/// </para>
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
public sealed class RowLevelSecurityInterceptor : SaveChangesInterceptor, IDbConnectionInterceptor, IDbCommandInterceptor
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
    public InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        ApplyPostgresGucs(command.Connection);
        return result;
    }

    /// <inheritdoc />
    public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask<InterceptionResult<DbDataReader>>(ReaderExecutingCoreAsync(command, result, cancellationToken));
    }

    /// <inheritdoc />
    public InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        ApplyPostgresGucs(command.Connection);
        return result;
    }

    /// <inheritdoc />
    public ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask<InterceptionResult<object>>(ScalarExecutingCoreAsync(command, result, cancellationToken));
    }

    /// <inheritdoc />
    public InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        ApplyPostgresGucs(command.Connection);
        return result;
    }

    /// <inheritdoc />
    public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask<InterceptionResult<int>>(NonQueryExecutingCoreAsync(command, result, cancellationToken));
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

    private async Task<InterceptionResult<DbDataReader>> ReaderExecutingCoreAsync(
        DbCommand command,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken)
    {
        await ApplyPostgresGucsAsync(command.Connection, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<InterceptionResult<object>> ScalarExecutingCoreAsync(
        DbCommand command,
        InterceptionResult<object> result,
        CancellationToken cancellationToken)
    {
        await ApplyPostgresGucsAsync(command.Connection, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<InterceptionResult<int>> NonQueryExecutingCoreAsync(
        DbCommand command,
        InterceptionResult<int> result,
        CancellationToken cancellationToken)
    {
        await ApplyPostgresGucsAsync(command.Connection, cancellationToken).ConfigureAwait(false);
        return result;
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

    private void ApplyPostgresGucs(DbConnection? connection)
    {
        if (connection is not NpgsqlConnection npgsqlConnection)
        {
            return;
        }

        using var command = BuildGucSetCommand(npgsqlConnection);
        command.ExecuteNonQuery();
    }

    private async Task ApplyPostgresGucsAsync(DbConnection? connection, CancellationToken cancellationToken)
    {
        if (connection is not NpgsqlConnection npgsqlConnection)
        {
            return;
        }

        await using var command = BuildGucSetCommand(npgsqlConnection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private NpgsqlCommand BuildGucSetCommand(NpgsqlConnection npgsqlConnection)
    {
        var command = npgsqlConnection.CreateCommand();
        command.CommandText =
            "SELECT set_config('app.account_id', @accountId, false), "
            + "set_config('app.user_id', @userId, false), "
            + "set_config('app.is_admin', @isAdmin, false);";
        command.Parameters.AddWithValue("@accountId", _accountContext.AccountId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("@userId", _accountContext.UserId?.ToString() ?? string.Empty);
        command.Parameters.AddWithValue("@isAdmin", _accountContext.IsAdministrator ? "true" : "false");
        return command;
    }

    /// <summary>
    /// Test-only seam that builds and returns the same GUC-setting
    /// <see cref="NpgsqlCommand"/> the production <see cref="ApplyPostgresGucs"/> would
    /// execute, without actually running it. Lets the unit test suite verify the SQL the
    /// interceptor would emit for a given ambient <see cref="IAccountContext"/> state
    /// — the property the IDbCommandInterceptor hooks protect (re-reading the live
    /// ambient context on every command, not a captured snapshot) — without standing up
    /// a real Npgsql connection. Visible to the unit test assembly only.
    /// </summary>
    internal NpgsqlCommand BuildGucSetCommandForTest(NpgsqlConnection npgsqlConnection)
    {
        return BuildGucSetCommand(npgsqlConnection);
    }
}
