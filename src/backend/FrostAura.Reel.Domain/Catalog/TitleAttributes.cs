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
    /// The enrich run (its job id) that holds this title for the batch in flight. Two runs never
    /// extract the same title at once: a run claims its batch with a conditional update before
    /// calling the model, and skips whatever another live run holds.
    /// </summary>
    public Guid? ClaimedBy { get; set; }

    /// <summary>The claim's lease. A run that dies mid-batch releases its titles when this passes.</summary>
    public DateTime? ClaimedUntil { get; set; }
}
