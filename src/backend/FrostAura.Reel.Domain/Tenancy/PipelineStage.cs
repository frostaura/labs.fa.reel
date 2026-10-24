namespace FrostAura.Reel.Domain.Tenancy;

/// <summary>
/// Per-account pipeline state machine: Linked → Ingesting → Extracting → Training →
/// Evaluated → FeedReady. Degraded is the hard-failure parking state (revoked token,
/// dead-lettered writes) — the UI prompts a re-link. NotEnoughRatings is the cold-start parking
/// state: nothing is broken, the account simply has fewer usable ratings than a model needs, and
/// the UI asks for ratings rather than a re-link. Subsequent retrains never regress the stage
/// below FeedReady.
///
/// <para>Persisted as a string (max 32). New values are appended, never renamed or reordered, and
/// every reader maps a value it does not know to a defined fallback, so an image from before a
/// value existed survives reading it.</para>
/// </summary>
public enum PipelineStage
{
    Linked,
    Ingesting,
    Extracting,
    Training,
    Evaluated,
    FeedReady,
    Degraded,

    /// <summary>
    /// Parked before training because the account has fewer usable movie/show ratings than the
    /// trainer's floor. Not a failure: the next Train after enough ratings arrive advances it.
    /// </summary>
    NotEnoughRatings,
}
