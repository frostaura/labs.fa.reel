namespace FrostAura.Reel.Application.Search.AskReel;

/// <summary>
/// What a turn has put on the wire so far — held OUTSIDE the pipeline so it survives the pipeline
/// being cancelled.
///
/// <para><b>Why it exists.</b> A follow-up typed over a live turn aborts it, and an aborted turn
/// used to be recorded as <c>Failed/client_aborted</c> with no text and no cards — while the user had
/// been looking at its cards for fifty seconds (A-6, B-3). "None of the above" then pointed at
/// nothing: the next turn's history held no reply and its no-repeat set held none of those titles,
/// so two of them came straight back. The endpoint owns one of these per turn, the service writes to
/// it at the moment each frame goes out, and on a cancellation the endpoint persists what it holds:
/// the reply that was shown and every card that was shown, as a replayable record.</para>
///
/// <para>Thread-safe: the service writes from the turn's own flow while the heartbeat reads the last
/// phase from a timer.</para>
/// </summary>
public sealed class AskTurnProgress
{
    private readonly object _gate = new();
    private readonly List<LiveSearchExpansionService.TurnCard> _cards = [];
    private readonly HashSet<Guid> _cardIds = [];
    private string? _assistantText;
    private bool _degraded;
    private object? _lastPhase;

    /// <summary>The reply the user was shown, once it was shown.</summary>
    public string? AssistantText
    {
        get
        {
            lock (_gate)
            {
                return _assistantText;
            }
        }
    }

    /// <summary>Whether the reply that was shown is the degraded copy.</summary>
    public bool Degraded
    {
        get
        {
            lock (_gate)
            {
                return _degraded;
            }
        }
    }

    /// <summary>The last <c>phase</c> payload, for the heartbeat to repeat. Null before the first.</summary>
    public object? LastPhase
    {
        get
        {
            lock (_gate)
            {
                return _lastPhase;
            }
        }
    }

    /// <summary>Every card shown so far, in the order shown, with ranks 0..n-1.</summary>
    public IReadOnlyList<LiveSearchExpansionService.TurnCard> Cards
    {
        get
        {
            lock (_gate)
            {
                return [.. _cards];
            }
        }
    }

    /// <summary>Records the reply as it goes out.</summary>
    public void Replied(string? text, bool degraded)
    {
        lock (_gate)
        {
            _assistantText = text;
            _degraded = degraded;
        }
    }

    /// <summary>Records a phase as it goes out.</summary>
    public void Phase(object payload)
    {
        lock (_gate)
        {
            _lastPhase = payload;
        }
    }

    /// <summary>
    /// Records a card as it goes out. The rank is assigned here, in emission order, so a partial
    /// record's ranks are exactly the order the user saw. A title already recorded is ignored.
    /// </summary>
    public void Shown(LiveSearchExpansionService.TurnCard card)
    {
        lock (_gate)
        {
            if (_cardIds.Add(card.TitleId))
            {
                _cards.Add(card with { Rank = _cards.Count });
            }
        }
    }
}
