namespace FrostAura.Reel.Domain.Sync;

public enum ApiProvider
{
    Trakt,
    Tmdb,
    OpenRouter,
}

/// <summary>
/// The purposes an OpenRouter/LLM call can be made for. Kept as constants rather than an enum so
/// the column stays readable in ad-hoc SQL and a new purpose does not force a migration.
/// </summary>
public static class ApiUsagePurpose
{
    public const string ExtractAttributes = "extract-attributes";
    public const string SearchInterpret = "search-interpret";
    public const string SearchAgent = "search-agent";
    public const string Rerank = "rerank";
    public const string Embed = "embed";
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
