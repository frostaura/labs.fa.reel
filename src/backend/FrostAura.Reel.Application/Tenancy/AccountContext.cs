namespace FrostAura.Reel.Application.Tenancy;

/// <summary>
/// Scoped holder — one per HTTP request / per job execution scope.
///
/// <para>The DI container builds it with the configured <see cref="UnscopedReadPolicy"/>. A context
/// built by hand (<c>new AccountContext()</c>: test fixtures seeding data, the design-time factory
/// behind <c>dotnet ef</c>) gets <see cref="UnscopedReadPolicy.Permissive"/>, because it is tooling
/// standing in for system scope, never a request or a job.</para>
/// </summary>
public class AccountContext(UnscopedReadPolicy policy) : IAccountContext
{
    private int _systemDepth;

    public AccountContext()
        : this(UnscopedReadPolicy.Permissive)
    {
    }

    public Guid? AccountId { get; private set; }

    public bool IsSystemScope => Volatile.Read(ref _systemDepth) > 0;

    public void SetAccount(Guid accountId) => AccountId = accountId;

    public void Clear() => AccountId = null;

    public IDisposable EnterSystemScope()
    {
        Interlocked.Increment(ref _systemDepth);
        return new SystemScopeHandle(this);
    }

    public bool PermitUnscopedRead() => AccountId is not null || IsSystemScope || policy.PermitUnscoped();

    private sealed class SystemScopeHandle(AccountContext owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            // Idempotent: a handle disposed twice must not close a scope somebody else opened.
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._systemDepth);
            }
        }
    }
}
