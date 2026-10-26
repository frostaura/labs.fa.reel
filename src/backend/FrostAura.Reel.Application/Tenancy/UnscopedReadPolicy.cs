using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Application.Tenancy;

/// <summary>What a query over an account-scoped entity does when no account is pinned and no system scope is open.</summary>
public enum UnscopedReadMode
{
    /// <summary>Read every account's rows, silently. The pre-J-11 behaviour; tooling only.</summary>
    Allow,

    /// <summary>
    /// Read every account's rows and log the call site, once per site per process. The rollout
    /// mode: it is how the paths that still need <see cref="IAccountContext.EnterSystemScope"/> are
    /// found on a running stack without breaking any of them.
    /// </summary>
    Warn,

    /// <summary>Throw <see cref="UnscopedTenantQueryException"/>. Fail closed — the J-11 target.</summary>
    Throw,
}

/// <summary>
/// The process-wide answer to "may an unpinned query read every tenant?" — one singleton, so the
/// once-per-call-site log is once per process rather than once per request.
/// </summary>
public sealed class UnscopedReadPolicy(UnscopedReadMode mode, ILogger<UnscopedReadPolicy>? logger = null)
{
    /// <summary>
    /// <c>Tenancy__UnscopedReads</c> = <c>Allow</c> | <c>Warn</c> | <c>Throw</c>. Unset means
    /// <see cref="DefaultMode"/>; an unreadable value also means the default, with a warning,
    /// because a typo must never turn tenancy off.
    /// </summary>
    public const string ConfigurationKey = "Tenancy:UnscopedReads";

    /// <summary>
    /// The mode a process runs in when nothing is configured. <see cref="UnscopedReadMode.Warn"/>
    /// until every owner has threaded system scope through its own paths (WS-6 task 7, step 2);
    /// the last step of the change is flipping this to <see cref="UnscopedReadMode.Throw"/>.
    /// </summary>
    public const UnscopedReadMode DefaultMode = UnscopedReadMode.Warn;

    /// <summary>How many distinct call sites are remembered and logged; a process past it has a census to read already.</summary>
    private const int SiteMemory = 512;

    private readonly ILogger _logger = (ILogger?)logger ?? NullLogger.Instance;
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);

    /// <summary>For contexts built by hand: fixtures and the design-time factory, which stand in for system scope.</summary>
    public static UnscopedReadPolicy Permissive { get; } = new(UnscopedReadMode.Allow);

    public UnscopedReadMode Mode { get; } = mode;

    /// <summary>The configured policy; with no configuration at all (a bare test container), <see cref="DefaultMode"/>.</summary>
    public static UnscopedReadPolicy FromConfiguration(IConfiguration? configuration, ILogger<UnscopedReadPolicy>? logger)
    {
        var raw = configuration?[ConfigurationKey];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new UnscopedReadPolicy(DefaultMode, logger);
        }

        if (Enum.TryParse<UnscopedReadMode>(raw.Trim(), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
        {
            return new UnscopedReadPolicy(parsed, logger);
        }

        logger?.LogWarning(
            "{Key} has an unreadable value; running as {Mode}. Use Allow, Warn or Throw.", ConfigurationKey, DefaultMode);
        return new UnscopedReadPolicy(DefaultMode, logger);
    }

    /// <summary>Called only for an unpinned query outside system scope. Returns true or throws.</summary>
    public bool PermitUnscoped()
    {
        switch (Mode)
        {
            case UnscopedReadMode.Allow:
                return true;
            case UnscopedReadMode.Throw:
                throw new UnscopedTenantQueryException(CallSite());
            default:
                var site = CallSite();
                if (_reported.Count < SiteMemory && _reported.TryAdd(site, 0))
                {
                    _logger.LogWarning(
                        "Unscoped tenant query at {CallSite}: no account pinned and no system scope, so it read every account's rows. "
                        + "Pin an account or wrap the path in IAccountContext.EnterSystemScope() before {Key} becomes Throw.",
                        site, ConfigurationKey);
                }

                return true;
        }
    }

    /// <summary>
    /// The first Reel frame outside the tenancy and persistence plumbing — the code that ran the
    /// query. Async state-machine frames are named after the method that owns them.
    /// </summary>
    internal static string CallSite()
    {
        var frames = new StackTrace(skipFrames: 2, fNeedFileInfo: false).GetFrames();
        foreach (var frame in frames)
        {
            var method = frame.GetMethod();
            var type = method?.DeclaringType;
            if (type is null)
            {
                continue;
            }

            // MoveNext of <Method>d__12 nested in the real class.
            var owner = type;
            var name = method!.Name;
            if (type.Name.StartsWith('<') && type.DeclaringType is { } outer)
            {
                owner = outer;
                var close = type.Name.IndexOf('>', StringComparison.Ordinal);
                name = close > 1 ? type.Name[1..close] : name;
            }

            var ns = owner.Namespace ?? string.Empty;
            if (!ns.StartsWith("FrostAura.Reel", StringComparison.Ordinal)
                || ns.StartsWith("FrostAura.Reel.Application.Tenancy", StringComparison.Ordinal)
                || ns.StartsWith("FrostAura.Reel.Infrastructure.Persistence", StringComparison.Ordinal))
            {
                continue;
            }

            return $"{owner.FullName}.{name}";
        }

        return "(unknown call site)";
    }
}

/// <summary>
/// A query over account-scoped data ran with no account pinned and no system scope, under
/// <see cref="UnscopedReadMode.Throw"/>. The fix is never to catch it: pin the account the work is
/// for (<see cref="IAccountContext.SetAccount"/>), or, for deliberate cross-account work, wrap the
/// path in <see cref="IAccountContext.EnterSystemScope"/>.
/// </summary>
public sealed class UnscopedTenantQueryException(string callSite)
    : InvalidOperationException(
        $"Account-scoped query with no account pinned and no system scope at {callSite}. "
        + "Pin an account (IAccountContext.SetAccount) or wrap deliberate cross-account work in IAccountContext.EnterSystemScope().")
{
    public string CallSite { get; } = callSite;
}
