namespace FrostAura.Reel.Application.Tenancy;

/// <summary>
/// Ambient account scope for the current unit of work (one HTTP request, one job execution).
///
/// <list type="bullet">
/// <item><b>HTTP requests:</b> pinned from the validated session JWT by AccountResolutionMiddleware.</item>
/// <item><b>Account jobs:</b> pinned by the job runner to the job's account.</item>
/// <item><b>Deliberate cross-account work</b> (startup, migrations, global jobs, schedulers that sweep
/// every account, pre-auth lookups): wrapped in <see cref="EnterSystemScope"/>.</item>
/// </list>
///
/// <para><b>Fails closed (J-11).</b> A query over an account-scoped entity with no account pinned
/// and no system scope is a code path that forgot to say whose data it is. Under
/// <see cref="UnscopedReadMode.Throw"/> it throws <see cref="UnscopedTenantQueryException"/>
/// instead of silently reading every tenant's rows; under <see cref="UnscopedReadMode.Warn"/> it
/// reads everything, as before, and logs the call site once. The mode is
/// <see cref="UnscopedReadPolicy.ConfigurationKey"/>.</para>
/// </summary>
public interface IAccountContext
{
    Guid? AccountId { get; }

    /// <summary>True while at least one <see cref="EnterSystemScope"/> handle is open.</summary>
    bool IsSystemScope { get; }

    void SetAccount(Guid accountId);

    void Clear();

    /// <summary>
    /// Declares that this unit of work reads across accounts on purpose, until the returned handle is
    /// disposed. Nestable; disposing restores the previous state. It does not change
    /// <see cref="AccountId"/>: a pinned account still narrows every scoped query, so the scope only
    /// matters to a query that runs with no account pinned.
    /// </summary>
    IDisposable EnterSystemScope();

    /// <summary>
    /// Asked by the persistence layer each time a query over an account-scoped entity runs with no
    /// account pinned: true lets it read every account's rows, false is never returned (the policy
    /// throws instead). Never call it to make a decision of your own — pin an account or enter
    /// system scope.
    /// </summary>
    bool PermitUnscopedRead();
}
