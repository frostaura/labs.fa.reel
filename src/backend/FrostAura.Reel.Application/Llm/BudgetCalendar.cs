using System.Collections.Concurrent;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Sync;
using FrostAura.Reel.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace FrostAura.Reel.Application.Llm;

/// <summary>
/// Which calendar day an LLM call's spend counts against (WS-1 task 12, ADD-22).
///
/// <para><b>Per-account budget days follow the account's time zone.</b> Every ceiling that is per
/// account — the Ask allowances, the re-rank family, cross-reference and the lens (the purposes
/// <see cref="IsAccountMetered"/> names) — resets at midnight where the account lives, not at
/// midnight UTC, which for a viewer at +02:00 was 02:00 and put an evening's spending on two
/// different days. The ledger stamps those rows with the account-local date, and every reader of
/// those ceilings asks this calendar for "today", so the two always agree.</para>
///
/// <para><b>Everything else stays on the UTC day</b>: catalogue-wide work (extraction, embeddings,
/// seeding), probes, rows with no account, and any purpose without a per-account ceiling. Their
/// budgets sum across accounts, and a global ceiling read by one calendar must be written by
/// the same one.</para>
///
/// <para><b>Where the zone comes from.</b> The account's IANA zone id in
/// <see cref="Domain.Tenancy.AccountSettings.TimeZone"/> (ADD-22, WS-8's M7: a field in the
/// settings jsonb, written by <c>PUT /api/settings</c>, not a column), resolved by
/// <see cref="Domain.Tenancy.AccountTimeZone.Resolve"/> — the same rule the settings API reports
/// as <c>effectiveTimeZone</c>. An unknown or unreadable zone, and an account that has none, all
/// mean UTC — the behaviour before this change. A zone is cached for <see cref="ZoneLifetime"/>;
/// <see cref="Forget"/> drops it at once when the account changes it.</para>
/// </summary>
public interface IBudgetCalendar
{
    /// <summary>
    /// Today's ledger day for a call: the account-local date when <paramref name="accountId"/> is
    /// set and <paramref name="purpose"/> has a per-account ceiling, the UTC date otherwise. Never
    /// blocks on a cached account; an uncached one is read once.
    /// </summary>
    DateTime DayFor(Guid? accountId, string? purpose);

    /// <summary>Today in the account's time zone — the day its per-account ceilings are counted on.</summary>
    Task<DateTime> TodayAsync(Guid accountId, CancellationToken ct);

    /// <summary>Drops a cached zone, so the account's next call reads the zone it now has.</summary>
    void Forget(Guid accountId);
}

/// <inheritdoc />
public sealed class BudgetCalendar : IBudgetCalendar
{
    /// <summary>How long a read zone is trusted before it is read again.</summary>
    public static readonly TimeSpan ZoneLifetime = TimeSpan.FromMinutes(10);

    /// <summary>A calendar that is always UTC: for code built by hand, which has no accounts table to read.</summary>
    public static IBudgetCalendar Utc { get; } = new BudgetCalendar(zones: null);

    private readonly IAccountZoneSource? _zonesSource;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, (TimeZoneInfo Zone, DateTimeOffset ReadAt)> _zones = new();

    public BudgetCalendar(IAccountZoneSource? zones, TimeProvider? time = null, ILogger<BudgetCalendar>? logger = null)
    {
        _zonesSource = zones;
        _time = time ?? TimeProvider.System;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// True for the purposes whose ceiling is per account: everything the reserve-before-call gate
    /// meters (the Ask purposes, the re-rank family, cross-reference) and the lens.
    /// </summary>
    public static bool IsAccountMetered(string? purpose) =>
        purpose is not null && (LlmCallGate.IsGated(purpose) || purpose == ApiUsagePurpose.Lens);

    /// <summary>The calendar date in <paramref name="zone"/> at <paramref name="now"/>, as the ledger stores days.</summary>
    public static DateTime LocalDay(DateTimeOffset now, TimeZoneInfo zone) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(now, zone).Date, DateTimeKind.Utc);

    /// <summary>
    /// A stored zone id as a zone, by <see cref="AccountTimeZone.Resolve"/> so the ledger day and the
    /// settings API's <c>effectiveTimeZone</c> can never disagree; null, blank or unknown is UTC.
    /// </summary>
    public static TimeZoneInfo ParseZone(string? id) => AccountTimeZone.Resolve(id?.Trim());

    /// <inheritdoc />
    public DateTime DayFor(Guid? accountId, string? purpose)
    {
        var now = _time.GetUtcNow();
        if (accountId is not { } account || !IsAccountMetered(purpose))
        {
            return LocalDay(now, TimeZoneInfo.Utc);
        }

        return LocalDay(now, Cached(account, now) ?? Read(account));
    }

    /// <inheritdoc />
    public async Task<DateTime> TodayAsync(Guid accountId, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        return LocalDay(now, Cached(accountId, now) ?? await ReadAsync(accountId, ct));
    }

    /// <inheritdoc />
    public void Forget(Guid accountId) => _zones.TryRemove(accountId, out _);

    private TimeZoneInfo? Cached(Guid account, DateTimeOffset now) =>
        _zones.TryGetValue(account, out var entry) && now - entry.ReadAt < ZoneLifetime ? entry.Zone : null;

    private async Task<TimeZoneInfo> ReadAsync(Guid account, CancellationToken ct) =>
        _zonesSource is null ? TimeZoneInfo.Utc : Remember(account, await _zonesSource.ZoneIdAsync(account, ct));

    /// <summary>
    /// The synchronous read, for the recorder's hot path. Practically never taken: every per-account
    /// ceiling is checked (<see cref="TodayAsync"/>) before its call is sent, which caches the zone
    /// the recorder then stamps the row with.
    /// </summary>
    private TimeZoneInfo Read(Guid account)
    {
        if (_zonesSource is null)
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return Remember(account, _zonesSource.ZoneId(account));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The recorder must never lose a row over a calendar read; the UTC day is the old behaviour.
            _logger.LogWarning(ex, "Budget day for account {AccountId} fell back to UTC: its time zone could not be read.", account);
            return TimeZoneInfo.Utc;
        }
    }

    private TimeZoneInfo Remember(Guid account, string? id)
    {
        var zone = ParseZone(id);
        if (!string.IsNullOrWhiteSpace(id) && !TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out _))
        {
            _logger.LogWarning("Account {AccountId} has an unknown time zone {Zone}; its budget days run on UTC.", account, id);
        }

        _zones[account] = (zone, _time.GetUtcNow());
        return zone;
    }

}

/// <summary>Where an account's IANA zone id is read from. Null means the account has none.</summary>
public interface IAccountZoneSource
{
    Task<string?> ZoneIdAsync(Guid accountId, CancellationToken ct);

    /// <summary>The same read, synchronously, for the recorder's rare cache miss.</summary>
    string? ZoneId(Guid accountId);
}

/// <summary>
/// Reads the account's <see cref="AccountSettings.TimeZone"/> (ADD-22, M7) through a fresh scope.
///
/// <para><b>Why the settings document, not a column.</b> M7 landed the zone as a field in the
/// account's owned settings jsonb (no migration), and <c>PUT /api/settings</c> writes it there. The
/// first version of this source probed for an <c>Accounts.Timezone</c> column that was never added,
/// so every account read as UTC even after the user set a zone (R1-14) — while Ask told them the
/// allowance resets "at midnight in your account's time zone". The <c>Accounts</c> table carries no
/// tenancy filter, so the unpinned scope reads it safely.</para>
/// </summary>
public sealed class AccountZoneSource(IServiceScopeFactory scopes) : IAccountZoneSource
{
    public async Task<string?> ZoneIdAsync(Guid accountId, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IReelDbContext>();
        return await db.Accounts.AsNoTracking()
            .Where(a => a.Id == accountId)
            .Select(a => a.Settings.TimeZone)
            .FirstOrDefaultAsync(ct);
    }

    public string? ZoneId(Guid accountId)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<IReelDbContext>();
        return db.Accounts.AsNoTracking()
            .Where(a => a.Id == accountId)
            .Select(a => a.Settings.TimeZone)
            .FirstOrDefault();
    }
}
