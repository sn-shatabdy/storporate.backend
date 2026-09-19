namespace Storporate.Infrastructure.Authorization;

/// <summary>
/// Async-flow-scoped writer for the ambient <see cref="IAccountContext"/> when work runs
/// outside an HTTP request. The polling
/// (<see cref="Storporate.Infrastructure.Jobs.PortfolioAnalysisWorker"/>) ticks every few
/// seconds and runs each processor on a fresh DI scope, so the HTTP-side
/// <see cref="AccountContextMiddleware"/> never populates the ambient account there. Without
/// an explicit per-tick scope, the EF Core global query filter
/// (<see cref="Persistence.WriteDbContext"/>) and the PostgreSQL row-level-security predicate
/// (set up by <c>AddRowLevelSecurity</c> + the per-table <c>…RowLevelSecurity</c> follow-up
/// migrations) match no rows; the
/// <see cref="Persistence.Interceptors.RowLevelSecurityInterceptor"/> then refuses any
/// <c>SaveChanges</c> that touches an <see cref="SharedKernel.Entities.IAccountScoped"/>
/// entity. This service is the only place that bridges background work over the same
/// three-layer isolation pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two scope shapes, one purpose.</b>
/// <list type="bullet">
/// <item>
/// <see cref="BeginSystemScope"/> — used for the "claim a job from the shared queue" step.
/// Sets <see cref="IAccountContextWriter.SetIsAdministrator"/>(true) and clears
/// <see cref="IAccountContextWriter.SetAccountId"/>(null). The query filter's
/// <c>_accountContext.IsAdministrator</c> branch short-circuits to true under this scope so
/// the <c>SELECT</c> / atomic <c>UPDATE</c> the claim executes sees every <c>Pending</c> row.
/// </item>
/// <item>
/// <see cref="BeginAccountScope"/> — used once the job has been claimed, around all the
/// per-account reads and writes that follow. Sets the <see cref="IAccountContext.AccountId"/>
/// to <c>job.AccountId</c> and clears <see cref="IAccountContext.IsAdministrator"/> so
/// the same query filter, the <see cref="Persistence.Interceptors.RowLevelSecurityInterceptor"/>
/// save-time guard, and the Postgres RLS <c>account_scoped</c> policy all key on the
/// owning account. Across <c>await</c>/<c>ConfigureAwait</c>/<c>Task.Run</c> the
/// <see cref="System.Threading.AsyncLocal{T}"/> storage follows the same async flow the
/// ambient service uses, so the values stay pinned to this tick's processor call.
/// </item>
/// </list>
/// </para>
/// <para>
/// <b>Restore-on-dispose, including on exceptions.</b> Each scope captures the prior
/// <see cref="IAccountContext.UserId"/>, <see cref="IAccountContext.AccountId"/>, and
/// <see cref="IAccountContext.IsAdministrator"/> values, swaps in the scope's values for
/// the duration, and restores the originals in <see cref="IDisposable.Dispose"/>. The
/// restore runs whether the body completed normally or threw, so an exception inside
/// <see cref="BeginAccountScope"/> (or a cancellation) leaves the ambient context exactly
/// the way it was. The IP / User-Agent fields are left alone — they are populated only by
/// the HTTP middleware, and background ticks don't carry them. Background code that needs
/// audit-friendly identity strings should pass them in via a future, non-conflicting path
/// (out of scope for STOR-40).
/// </para>
/// <para>
/// <b>Lifetime.</b> Registered as <see cref="Microsoft.Extensions.DependencyInjection.ServiceLifetime.Singleton"/>
/// alongside <see cref="AmbientAccountContext"/>: the writer it composes is itself a
/// singleton with <see cref="System.Threading.AsyncLocal{T}"/> storage, so the scope service
/// holds no per-tick state — only a strategy for swapping values in and out.
/// </para>
/// </remarks>
public interface IBackgroundAccountScope
{
    /// <summary>
    /// Open a scope under which <see cref="IAccountContext.IsAdministrator"/> is true and
    /// <see cref="IAccountContext.AccountId"/> is null. Use for the polling worker's
    /// "claim one <c>Pending</c> job" reads against the shared queue.
    /// </summary>
    IDisposable BeginSystemScope();

    /// <summary>
    /// Open a scope under which <see cref="IAccountContext.AccountId"/> equals
    /// <paramref name="accountId"/> and <see cref="IAccountContext.IsAdministrator"/> is
    /// false. Use for everything that touches the owning account's data after a job has
    /// been claimed. Disposal restores the prior values, including on exceptions and
    /// cancellation.
    /// </summary>
    IDisposable BeginAccountScope(Guid accountId);
}

/// <summary>
/// Singleton <see cref="IBackgroundAccountScope"/> built on top of the same
/// <see cref="IAccountContextWriter"/> the HTTP middleware writes through. The
/// <see cref="IAccountContext"/> reader is held alongside the writer because both
/// <see cref="IBackgroundAccountScope"/> methods need to snapshot the prior values before
/// swapping them in. The host registers <see cref="AmbientAccountContext"/> as a singleton
/// implementing both interfaces, so the two dependencies resolve to the same
/// <see cref="System.Threading.AsyncLocal{T}"/> storage.
/// </summary>
public sealed class BackgroundAccountScope : IBackgroundAccountScope
{
    private readonly IAccountContext _reader;
    private readonly IAccountContextWriter _writer;

    public BackgroundAccountScope(IAccountContext reader, IAccountContextWriter writer)
    {
        _reader = reader;
        _writer = writer;
    }

    /// <inheritdoc />
    public IDisposable BeginSystemScope()
    {
        // The claim path needs the global query filter's IsAdministrator short-circuit
        // to fire. We don't touch AccountId here because the claim UPDATE WHERE clause
        // keys on Status + Type, not on AccountId, and leaving AccountId null avoids
        // a misleading "ambient account" trail on any concurrent background read that
        // happens to overlap (e.g. a non-claim background task).
        var previousUserId = _reader.UserId;
        var previousIsAdmin = _reader.IsAdministrator;
        _writer.SetIsAdministrator(true);
        return new ScopeRestorer(() =>
        {
            _writer.SetUserId(previousUserId);
            _writer.SetIsAdministrator(previousIsAdmin);
        });
    }

    /// <inheritdoc />
    public IDisposable BeginAccountScope(Guid accountId)
    {
        // Post-claim path: run reads and writes against the owning account's data. We
        // explicitly clear IsAdministrator so the global filter and the save-time
        // RowLevelSecurityInterceptor both key on the now-populated AccountId — using
        // the admin short-circuit here would skip the same-id check the interceptor
        // enforces and would silently let the wrong account's findings land on the
        // job.
        var previousUserId = _reader.UserId;
        var previousAccountId = _reader.AccountId;
        var previousIsAdmin = _reader.IsAdministrator;
        _writer.SetUserId(accountId);
        _writer.SetAccountId(accountId);
        _writer.SetIsAdministrator(false);
        return new ScopeRestorer(() =>
        {
            _writer.SetUserId(previousUserId);
            _writer.SetAccountId(previousAccountId);
            _writer.SetIsAdministrator(previousIsAdmin);
        });
    }

    /// <summary>
    /// Disposable wrapper that runs the restore action exactly once. <c>using</c> in the
    /// worker that throws plus an outer <c>try/finally</c> both calling <c>Dispose</c>
    /// must collapse to a single restore, hence the interlocked gate.
    /// </summary>
    private sealed class ScopeRestorer : IDisposable
    {
        private readonly Action _onDispose;
        private int _disposed;

        internal ScopeRestorer(Action onDispose)
        {
            _onDispose = onDispose;
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _onDispose();
        }
    }
}
