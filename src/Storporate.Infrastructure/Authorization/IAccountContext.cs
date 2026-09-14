namespace Storporate.Infrastructure.Authorization;

/// <summary>
/// Reader-side view of the ambient account context for the current request. Populated by
/// <see cref="AccountContextMiddleware"/> after authentication runs (so the JWT <c>sub</c>
/// claim has already been validated into <see cref="System.Security.Claims.ClaimsPrincipal.User"/>)
/// and read by every layer of the authorization stack that needs to know "who is calling, on
/// whose behalf" — the <c>PermissionAuthorizationHandler</c> (Phase 3), the EF Core global
/// query filter and save-time interceptor (Phase 4), and the PostgreSQL row-level-security
/// session-GUC writer (Phase 5).
/// </summary>
/// <remarks>
/// <para>
/// Three pieces of state, not two: <see cref="UserId"/> (the JWT subject — always present when
/// the request is authenticated), <see cref="AccountId"/> (the workspace the call is being made
/// <em>in</em> — present when the route binds an <c>{accountId}</c> route value or when an
/// explicit ambient-account plumbing path sets it), and <see cref="IsAdministrator"/>
/// (precomputed from the JWT <c>actor_type</c> claim so the handler can short-circuit the
/// workspace-isolation bypass in one branch rather than re-reading the claim each time).
/// </para>
/// <para>
/// The split between an <see cref="IAccountContext"/> reader interface and an
/// <see cref="IAccountContextWriter"/> writer interface is deliberate: the
/// <c>PermissionAuthorizationHandler</c> only needs read access, but the
/// <c>AccountContextMiddleware</c> needs to <em>set</em> the values. Splitting the contract
/// means the handler cannot accidentally (or maliciously) mutate the ambient state for a
/// downstream consumer, and unit tests of the handler can hand it a read-only fake without
/// standing up the AsyncLocal plumbing.
/// </para>
/// </remarks>
public interface IAccountContext
{
    /// <summary>The authenticated caller's <see cref="SharedKernel.Entities.User"/> id (the JWT
    /// <c>sub</c> claim). Null only for the brief, pre-authentication window before
    /// <see cref="AccountContextMiddleware"/> runs, or for requests that explicitly opted out
    /// of authentication via <see cref="Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute"/>.</summary>
    Guid? UserId { get; }

    /// <summary>The workspace this call is being made in. Equal to <see cref="UserId"/> for the
    /// four self-service actor types (workspace = account, per the STOR-62 plan, Context &amp;
    /// Findings); resolved from an <c>{accountId}</c> route value when an endpoint acts on
    /// another account's behalf. Null for self-service endpoints that don't bind
    /// <c>{accountId}</c> — they implicitly operate in the caller's own workspace.</summary>
    Guid? AccountId { get; }

    /// <summary>True iff the authenticated caller's <see cref="SharedKernel.Entities.User.ActorType"/>
    /// is <see cref="SharedKernel.Entities.ActorTypes.Administrator"/>. Precomputed once at
    /// middleware time from the JWT <c>actor_type</c> claim so every consumer can short-circuit
    /// the workspace-isolation bypass in a single bool check.</summary>
    bool IsAdministrator { get; }
}

/// <summary>
/// Writer-side counterpart to <see cref="IAccountContext"/> — only
/// <see cref="AccountContextMiddleware"/> needs this. Kept on its own interface so the
/// authorization handler can't accidentally (or via a careless test setup) mutate the ambient
/// account context for downstream layers.
/// </summary>
public interface IAccountContextWriter
{
    /// <summary>Sets the ambient <see cref="IAccountContext.UserId"/>. Called by
    /// <see cref="AccountContextMiddleware"/> after the JWT bearer middleware has populated
    /// <see cref="System.Security.Claims.ClaimsPrincipal.User"/>.</summary>
    void SetUserId(Guid? value);

    /// <summary>Sets the ambient <see cref="IAccountContext.AccountId"/>. Called by
    /// <see cref="AccountContextMiddleware"/> when the matched route binds an
    /// <c>{accountId}</c> route value.</summary>
    void SetAccountId(Guid? value);

    /// <summary>Sets the ambient <see cref="IAccountContext.IsAdministrator"/> flag. Called by
    /// <see cref="AccountContextMiddleware"/> from the JWT <c>actor_type</c> claim (so no DB
    /// round-trip is needed at request time — see the middleware's doc comment for the
    /// rationale).</summary>
    void SetIsAdministrator(bool value);
}
