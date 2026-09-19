namespace FrostAura.Reel.Domain.Sync;

public enum ApiProvider
{
    Trakt,
    Tmdb,
    OpenRouter,
}

/// <summary>
/// The purposes an external call can be made for — mostly OpenRouter/LLM work, and since the
/// catalogue bulk seed also <see cref="Seed"/>, which is a TMDB purpose. Kept as constants rather
/// than an enum so the column stays readable in ad-hoc SQL and a new purpose does not force a
/// migration.
/// </summary>
public static class ApiUsagePurpose
{
    public const string ExtractAttributes = "extract-attributes";
    public const string SearchInterpret = "search-interpret";
    public const string SearchAgent = "search-agent";

    /// <summary>
    /// The LEGACY re-rank purpose, written by every re-rank caller before the family was split.
    ///
    /// <para>It is kept, and is still the default on the port, for one reason: the family ceiling
    /// counts it. Historical rows carry this string, so a ceiling that summed only the four new
    /// members would read a day with real spend on it as empty. Nothing new should be written under
    /// it — pass one of <see cref="RerankFeed"/>, <see cref="RerankPage"/>, <see cref="RerankAsk"/>
    /// or <see cref="RerankExpand"/> — but a caller that forgets degrades to "counted, unattributed"
    /// rather than to "free".</para>
    /// </summary>
    public const string Rerank = "rerank";

    /// <summary>
    /// The feed build's own re-rank passes — the pre-assembly hero fit judgement, the low-fit hero
    /// swap, and the rail curation. Background work with nobody waiting on it, which is why this is
    /// the one member of the family that stops at a fraction of the ceiling: a feed build must
    /// never be able to starve the founder's own chat turn later the same day.
    /// </summary>
    public const string RerankFeed = "rerank-feed";

    /// <summary>The paginator's curation pass on <c>/feed/more</c> and rail pagination. Request path.</summary>
    public const string RerankPage = "rerank-page";

    /// <summary>An Ask Reel conversational turn's candidate re-rank. Request path, and the reason
    /// the family ceiling exists at all: it gets the full ceiling, never a fraction.</summary>
    public const string RerankAsk = "rerank-ask";

    /// <summary>
    /// A single-turn live-search expansion — the same service as <see cref="RerankAsk"/> entered
    /// without conversation history, i.e. a query expanded rather than a conversation continued.
    /// Split out because the two have different shapes: one is a person waiting mid-conversation,
    /// the other is a one-shot lookup, and a ceiling that could not tell them apart could not later
    /// be tuned for either.
    /// </summary>
    public const string RerankExpand = "rerank-expand";

    /// <summary>
    /// Every string the re-rank family ceiling sums, legacy included. Written out rather than
    /// prefix-matched on <c>"rerank"</c>, because a prefix rule would silently absorb any future
    /// purpose someone names <c>rerank-something</c> into a budget written before it existed.
    /// </summary>
    public static readonly string[] RerankFamily = [Rerank, RerankFeed, RerankPage, RerankAsk, RerankExpand];

    public const string Embed = "embed";

    /// <summary>Writing an account's second-person taste narrative. Weekly, one call per account.</summary>
    public const string TasteNarrative = "taste-narrative";

    /// <summary>
    /// Writing "through your lens" copy for a title. This is the purpose the per-account daily
    /// call and token ceilings are enforced against, so nothing else may borrow the string.
    /// </summary>
    public const string Lens = "lens";

    /// <summary>The founder-only LLM-judge pass over generated lens copy. Deliberately NOT
    /// <see cref="Lens"/>, so auditing a prompt version cannot eat the budget it is auditing.</summary>
    public const string LensAudit = "lens-audit";

    /// <summary>
    /// Judging the eligible pool against the viewer's own rated neighbours — the cross-reference
    /// rounds. The purpose the per-account daily call and token ceilings
    /// (<c>LLM_XREF_DAILY_CALLS</c> / <c>LLM_XREF_DAILY_TOKENS</c>) are enforced against, so
    /// nothing else may borrow the string. It never shares the re-rank family's ceiling: a cold
    /// pass over ten thousand titles would eat every other surface's allowance in an afternoon.
    /// </summary>
    public const string CrossReference = "cross-reference";

    /// <summary>The offline backtest of the cross-reference judge. Deliberately NOT
    /// <see cref="CrossReference"/> — the same rule as <see cref="LensAudit"/>: measuring the
    /// instrument must not be able to consume the budget the instrument runs on.</summary>
    public const string CrossReferenceEval = "cross-reference-eval";

    /// <summary>
    /// TMDB discover pages fetched by the catalogue bulk seed — the first non-LLM purpose on this
    /// ledger, and the reason the seed's spend is separable from the feed's. It is what
    /// <c>GET /api/dev/seed-catalog</c> reads for "pages used today": the seed is the only writer
    /// of this string, so a count of calls under it IS a count of discover pages.
    /// </summary>
    public const string Seed = "seed";
}

/// <summary>
/// Daily call and token counters per external provider — Fair-Use monitoring from day 1, surfaced
/// on the pipeline metrics view. The M4 exit criterion reads these (&lt;60% of budget at 6 accounts).
///
/// The ledger used to be one bucket per (Provider, Day) with <c>TokensUsed</c> written as a literal
/// NULL, which made "what does a feed build cost" and "what has this account spent" unanswerable —
/// and OpenRouter shared its single bucket with embeddings. The key is now
/// (Provider, Day, AccountId, Model, Purpose).
///
/// <b>Null handling, chosen deliberately:</b> the three new key parts stay genuinely nullable — a
/// Trakt call has no model and a catalogue-wide enrichment has no account, and a sentinel would
/// invent a fact. Postgres normally treats NULLs in a unique index as distinct, which would let the
/// 15s flush insert an unbounded pile of duplicate rows instead of upserting, so the index is
/// declared <c>NULLS NOT DISTINCT</c> (Postgres 15+). Two flushes with a null model collide, as
/// they must. This entity is deliberately NOT <see cref="Tenancy.IAccountScoped"/>: it is an
/// operations ledger read by global monitoring, and <see cref="AccountId"/> is a dimension on it,
/// not ownership of it.
/// </summary>
public class ExternalApiUsage
{
    public Guid Id { get; set; }

    public ApiProvider Provider { get; set; }

    public DateTime Day { get; set; }

    /// <summary>The account the call was made on behalf of; null for catalogue-wide or unattributed work.</summary>
    public Guid? AccountId { get; set; }

    /// <summary>The model id billed, e.g. <c>openai/gpt-5.4-mini</c>; null for non-LLM providers.</summary>
    public string? Model { get; set; }

    /// <summary>One of <see cref="ApiUsagePurpose"/>; null where the call has no LLM purpose.</summary>
    public string? Purpose { get; set; }

    public long CallCount { get; set; }

    /// <summary>Prompt + completion, kept for backwards compatibility with the existing readers.</summary>
    public long? TokensUsed { get; set; }

    public long? PromptTokens { get; set; }

    public long? CompletionTokens { get; set; }
}

/// <summary>
/// The counters that have not reached the day ledger yet. The recorder flushes every 15 seconds,
/// and a single lens job can spend its whole daily allowance well inside one of those windows — so
/// a budget gate that reads only the persisted table would let a burst straight through. Exposed
/// as a port because the gate lives in the application layer and the recorder in infrastructure.
/// </summary>
public interface IPendingApiUsage
{
    /// <summary>
    /// Calls and tokens recorded today for this (provider, account, purpose) and still in memory.
    /// Tokens are prompt + completion, and count as zero where the provider reported nothing —
    /// unknown spend is not free, but it is also not a number this gate can subtract.
    /// </summary>
    (long Calls, long Tokens) PendingToday(ApiProvider provider, Guid? accountId, string purpose);

    /// <summary>
    /// As <see cref="PendingToday"/>, summed across EVERY account and the null-account (global)
    /// bucket. This is the shape a catalogue-wide ceiling needs: a <c>Title</c> row is
    /// tenant-shared, so "what has extraction cost today" is one system number, and a gate that
    /// asked per account would admit N accounts' worth of spend against a budget written for one.
    /// </summary>
    (long Calls, long Tokens) PendingTodayAllAccounts(ApiProvider provider, string purpose);
}
