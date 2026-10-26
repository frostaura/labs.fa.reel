using FrostAura.Reel.Application.Configuration;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Application.Jobs;

/// <summary>What a daily ceiling had left when it was asked. <paramref name="Limit"/> of 0 means "closed".</summary>
public readonly record struct DailyAllowance(long Used, long Limit)
{
    public bool HasHeadroom => Used < Limit;

    public long Remaining => Math.Max(0, Limit - Used);
}

/// <summary>
/// The daily ceiling on TMDB hydration of the shared catalogue.
///
/// <para>
/// <b>Counted in titles, not tokens, because TMDB is free.</b> Hydration costs nothing but rate-gate
/// capacity — one call per title on the <c>Backfill</c> lane, which by construction cannot starve
/// interactive traffic. The ceiling therefore exists to pace the pipeline rather than to cap a bill:
/// enrichment costs money per hydrated title, so hydrating 35,000 titles in a weekend would present
/// the extraction budget with a backlog it needs a month to pay for, and would do it all in one
/// month rather than spread across the same month either way. Pacing supply at the rate the
/// expensive stage can absorb keeps the two in step.
/// </para>
///
/// <para>
/// <b>What "used" means, precisely.</b> It is titles whose <c>LastMetadataRefreshAt</c> landed today
/// — which includes a re-hydration of an already-known title, not only a first one. That is the
/// honest reading for a ceiling that exists to bound TMDB calls: a refresh costs the same call as a
/// first hydration. It also means the ceiling degrades safely, since the count can only ever
/// over-report work, never under-report it.
/// </para>
/// </summary>
public sealed class CatalogHydrationBudget(IReelDbContext db, IConfiguration? configuration)
{
    /// <summary>
    /// Titles hydrated per day, across the whole shared catalogue. <c>HYDRATE_MAX_TITLES_PER_DAY</c>.
    /// <para>
    /// 1,500 is chosen to sit just above what the extraction budget can afford in the same day
    /// (~1,360 titles at <c>EXTRACT_DAILY_TOKENS</c>), so hydration stays marginally ahead of
    /// enrichment and enrichment is never idle waiting for metadata. At 1,500/day a ~35,000-title
    /// seed is fully hydrated in ~24 days.
    /// </para>
    /// <para>
    /// The same ceiling also pays for the metadata refresh sweep (<c>MetadataRefreshPolicy</c>):
    /// recent releases weekly, everything else every 150 days. For a ~38,000-title catalogue that is
    /// a steady ~500 refreshes a day, so 1,500 keeps every cached TMDB record inside TMDB's
    /// six-month limit with room left for new supply. The global sweep logs the projection on every
    /// run and warns when a lowered value would break the limit.
    /// </para>
    /// </summary>
    public const string DailyTitlesKey = "HYDRATE_MAX_TITLES_PER_DAY";

    public const int DefaultDailyTitles = 1_500;

    public long DailyTitles => Math.Max(0, FeatureFlags.ReadInt(configuration, DailyTitlesKey, DefaultDailyTitles));

    public async Task<DailyAllowance> AllowanceAsync(DateTime now, CancellationToken ct)
    {
        var today = now.Date;
        var tomorrow = today.AddDays(1);
        var used = await db.Titles.CountAsync(
            t => t.LastMetadataRefreshAt >= today && t.LastMetadataRefreshAt < tomorrow, ct);
        return new DailyAllowance(used, DailyTitles);
    }
}

/// <summary>
/// The daily token ceilings on catalogue enrichment, enforced against the <c>ExternalApiUsages</c>
/// ledger plus the recorder's un-flushed in-memory counters.
///
/// <para>
/// <b>System-wide, not per account.</b> A <c>Title</c> row is tenant-shared: one extraction serves
/// every account for ever. A ceiling asked per account would therefore admit N accounts' worth of
/// spend against a budget written for one, while capping nothing, since a global sweep carries no
/// account at all. This is the opposite choice to <c>LensBudget</c>, and deliberately so — lens copy
/// is written FOR one account and is worthless to any other.
/// </para>
///
/// <para><b>The arithmetic, spelled out, because a ceiling nobody can re-derive gets raised by
/// accident.</b> Extraction runs on the founder-locked <c>openai/gpt-6-luna</c> at input pricing
/// of ~$0.10 per million tokens. <c>EXTRACT_DAILY_TOKENS</c> = 1,500,000 is therefore
/// ~$0.15/day, ~$4.50/month. One extraction is ~1,100 tokens (a ~900-token prompt
/// carrying the overview, genres and keywords, plus a ~200-token structured answer), so the ceiling
/// buys ~1,360 titles a day and a ~35,000-title seed is fully attributed in ~26 days. Embeddings are
/// an order of magnitude cheaper: <c>text-embedding-3-small</c> at ~$0.02 per million tokens makes
/// <c>EMBED_DAILY_TOKENS</c> = 3,000,000 about $0.06/day, and at ~130 tokens per title it clears
/// ~23,000 titles a day — far past what hydration can feed it, which is the point. Extraction is
/// the binding cost; embedding is not, and the two are separate settings so that stays visible.</para>
///
/// <para>
/// <b>Conservative and overridable on purpose.</b> The founder's real appetite for this spend is an
/// open question, so the default is set at the low end of defensible and both keys are read fresh on
/// every run. Raising either is one environment variable and takes effect on the next job.
/// </para>
/// </summary>
public sealed class CatalogEnrichmentBudget(
    IReelDbContext db, IConfiguration? configuration, IPendingApiUsage? pending = null)
{
    public const string ExtractTokensKey = "EXTRACT_DAILY_TOKENS";

    public const int DefaultExtractDailyTokens = 1_500_000;

    public const string EmbedTokensKey = "EMBED_DAILY_TOKENS";

    public const int DefaultEmbedDailyTokens = 3_000_000;

    /// <summary>
    /// Fallback tokens per extracted title, used only to turn a backlog into an ETA before the
    /// ledger has any real data to measure. Prompt (~900) + structured answer (~200).
    /// </summary>
    public const int AssumedExtractTokensPerTitle = 1_100;

    /// <summary>
    /// Tokens per embedded title. Not measurable from the ledger the way extraction is — the
    /// embeddings API batches 64 texts into ONE call, so calls do not map to titles — so it stays a
    /// stated assumption rather than a fabricated measurement. A title, year and overview is ~130
    /// tokens.
    /// </summary>
    public const int AssumedEmbedTokensPerTitle = 130;

    public long ExtractDailyTokens =>
        Math.Max(0, FeatureFlags.ReadInt(configuration, ExtractTokensKey, DefaultExtractDailyTokens));

    public long EmbedDailyTokens =>
        Math.Max(0, FeatureFlags.ReadInt(configuration, EmbedTokensKey, DefaultEmbedDailyTokens));

    public Task<DailyAllowance> ExtractAllowanceAsync(DateTime now, CancellationToken ct) =>
        AllowanceAsync(ApiUsagePurpose.ExtractAttributes, ExtractDailyTokens, now, ct);

    public Task<DailyAllowance> EmbedAllowanceAsync(DateTime now, CancellationToken ct) =>
        AllowanceAsync(ApiUsagePurpose.Embed, EmbedDailyTokens, now, ct);

    /// <summary>
    /// Measured tokens per extraction over the last <paramref name="days"/> days, or
    /// <see cref="AssumedExtractTokensPerTitle"/> where the ledger has nothing to say. Exact rather
    /// than approximate: the extractor records one ledger call per title, so tokens ÷ calls IS
    /// tokens per title.
    /// </summary>
    public async Task<int> ExtractTokensPerTitleAsync(DateTime now, int days, CancellationToken ct)
    {
        var since = now.Date.AddDays(-Math.Max(0, days));
        var rows = await db.ExternalApiUsages
            .Where(u => u.Provider == ApiProvider.OpenRouter
                && u.Purpose == ApiUsagePurpose.ExtractAttributes
                && u.Day >= since)
            .Select(u => new { u.CallCount, u.TokensUsed })
            .ToListAsync(ct);

        var calls = rows.Sum(r => r.CallCount);
        var tokens = rows.Sum(r => r.TokensUsed ?? 0);
        return calls <= 0 || tokens <= 0
            ? AssumedExtractTokensPerTitle
            : (int)Math.Max(1, tokens / calls);
    }

    private async Task<DailyAllowance> AllowanceAsync(
        string purpose, long limit, DateTime now, CancellationToken ct)
    {
        var today = now.Date;
        // The ledger is not IAccountScoped — AccountId is a dimension on an operations table, not
        // ownership of it — so every account's rows and the null-account global rows are all in
        // scope here without any filter to defeat.
        var used = await db.ExternalApiUsages
            .Where(u => u.Provider == ApiProvider.OpenRouter && u.Purpose == purpose && u.Day == today)
            .Select(u => u.TokensUsed)
            .ToListAsync(ct);

        // The persisted ledger flushes every 15 seconds, and one enrich run can spend a day's
        // allowance inside a single window — so the in-memory counters are not an optimisation here,
        // they are the difference between a gate and a delayed observation.
        var inFlight = pending?.PendingTodayAllAccounts(ApiProvider.OpenRouter, purpose) ?? (0L, 0L);
        return new DailyAllowance(used.Sum(t => t ?? 0) + inFlight.Tokens, limit);
    }
}
