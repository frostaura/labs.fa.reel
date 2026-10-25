namespace FrostAura.Reel.Domain.Catalog;

/// <summary>
/// LLM-derived taste attributes for one title — extracted once via OpenRouter, cached forever,
/// shared across every tenant (a major unit-economics lever). Scalars are 0–1.
/// </summary>
public class TitleAttributes
{
    public Guid TitleId { get; set; }

    public decimal Darkness { get; set; }

    /// <summary>0 = glacial slow-burn, 1 = frenetic.</summary>
    public decimal Pacing { get; set; }

    public decimal Complexity { get; set; }

    public decimal EmotionalIntensity { get; set; }

    public decimal Humor { get; set; }

    public decimal Optimism { get; set; }

    /// <summary>0 = single-protagonist, 1 = broad ensemble.</summary>
    public decimal EnsembleVsSolo { get; set; }

    /// <summary>Dominant tone keyword, e.g. "melancholic", "playful".</summary>
    public string? Tone { get; set; }

    /// <summary>Setting era keyword, e.g. "near-future", "period-victorian".</summary>
    public string? Era { get; set; }

    public string[] Themes { get; set; } = [];

    public string ExtractorModel { get; set; } = string.Empty;

    public int ExtractorVersion { get; set; }

    /// <summary>Full structured LLM output (jsonb) for audit/re-derivation.</summary>
    public string? RawJson { get; set; }

    public AttributeExtractionStatus Status { get; set; } = AttributeExtractionStatus.Pending;

    public int AttemptCount { get; set; }

    public DateTime? ExtractedAt { get; set; }

    /// <summary>
    /// When an enrich run last asked the model about this title, whatever came back. Null marks a
    /// row last touched by a build that did not stamp it — which is exactly how the one-off repairs
    /// recognise the pre-fix backlog without ever re-firing on a row the current handler wrote.
    /// </summary>
    public DateTime? LastAttemptAt { get; set; }

    /// <summary>
    /// Not before this instant: the time gate a failure sets instead of spending the attempt budget.
    /// A dropped connection is a fact about the network, not about the title, so it backs off by
    /// time (<see cref="TransportFailures"/> doubles the wait) and never charges
    /// <see cref="AttemptCount"/>. A failure on a row that already holds an answer sets it too,
    /// because that row must never be demoted to Failed.
    /// </summary>
    public DateTime? RetryAfter { get; set; }

    /// <summary>Consecutive transport failures since the last answer; drives the backoff, reset on success.</summary>
    public int TransportFailures { get; set; }

    /// <summary>
    /// Failed re-extractions of a row that already holds an answer, counted for the question named
    /// in <see cref="ReattemptQuestion"/> only. Such a row cannot be charged against
    /// <see cref="AttemptCount"/> without being demoted, so this is its attempt budget: once it
    /// reaches the handler's cap the current model at the current prompt version stops asking, and
    /// the row keeps its answer. Reset on success.
    /// </summary>
    public int ReattemptFailures { get; set; }

    /// <summary>
    /// The (model, prompt version) <see cref="ReattemptFailures"/> was counted against, as
    /// <c>model@vN</c>. A different model or a newer prompt is a different question, so it starts
    /// from zero. Null when no re-extraction has failed since the last answer.
    /// </summary>
    public string? ReattemptQuestion { get; set; }

    /// <summary>
    /// The enrich run that holds this title for the batch in flight — a fresh id per execution, not
    /// the job id, because a revived job shares its id with the live original. Two runs never
    /// extract the same title at once: a run claims its batch with a conditional update before
    /// calling the model, and skips whatever another live run holds.
    /// </summary>
    public Guid? ClaimedBy { get; set; }

    /// <summary>The claim's lease. A run that dies mid-batch releases its titles when this passes.</summary>
    public DateTime? ClaimedUntil { get; set; }
}
