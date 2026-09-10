using System.Diagnostics;
using System.Globalization;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// Where an Ask Reel turn's seconds actually went.
///
/// <para>Before this, "the chat feels slow" was unanswerable. A turn is eight distinct pieces of
/// work — interpret, discover, eligible, embed, score, rerank, blend, emit — with wildly different
/// costs and wildly different fixes, and the only figure anyone had was the wall clock. Every
/// latency claim about this surface was therefore a guess, and two of them were wrong about which
/// stage to optimise.</para>
///
/// <para>Stages ACCUMULATE. Discovery and eligibility run once per relaxation level, up to three
/// times a turn, and the number worth having is "how long did discovery cost this turn" rather than
/// three separate entries — so a name records once, with the total.</para>
///
/// <para>Every stage also opens an <see cref="Activity"/> on <see cref="ActivitySource"/>, so the
/// same boundaries are available to a tracing exporter without a second set of instrumentation to
/// keep in step. Nothing listens today; the source costs nothing when nothing does.</para>
/// </summary>
public sealed class AskStageTimings
{
    /// <summary>The tracing source every stage opens an activity on. Inert while nothing listens.</summary>
    public static readonly ActivitySource ActivitySource = new("FrostAura.Reel.AskReel");

    /// <summary>Turning the turn into a reply plus a discovery intent — the conversational LLM call.</summary>
    public const string Interpret = "interpret";

    /// <summary>Both discovery arms: the TMDB pull and the semantic kNN, across every relaxation level.</summary>
    public const string Discover = "discover";

    /// <summary>Narrowing the discovered ids to what this account may actually be shown.</summary>
    public const string Eligible = "eligible";

    /// <summary>Embedding whatever is not embedded yet, loading the vectors, and the ranking seed.</summary>
    public const string Embed = "embed";

    /// <summary>Hydrating the shortlist and predicting a rating for every card.</summary>
    public const string Score = "score";

    /// <summary>The per-title LLM fit pass — normally the largest single number here.</summary>
    public const string Rerank = "rerank";

    /// <summary>Combining similarity, prediction and fit, and applying the account's rating floor.</summary>
    public const string Blend = "blend";

    /// <summary>The terminal frame and the exposure write that follows it.</summary>
    public const string Emit = "emit";

    /// <summary>
    /// Canonical order — the order the work happens in, which is the order a reader wants it. The
    /// snapshot is built from this list, so a stage that never ran is simply absent rather than zero.
    /// </summary>
    public static readonly IReadOnlyList<string> Order =
        [Interpret, Discover, Eligible, Embed, Score, Rerank, Blend, Emit];

    private readonly Dictionary<string, double> _elapsedMs = new(StringComparer.Ordinal);

    /// <summary>
    /// Times everything until the returned handle is disposed, adding it to <paramref name="name"/>'s
    /// running total. Use as <c>using var _ = timings.Stage(AskStageTimings.Embed);</c>.
    /// </summary>
    public IDisposable Stage(string name) => new Scope(this, name);

    /// <summary>
    /// Whole milliseconds per stage that actually ran, in <see cref="Order"/>. Milliseconds because
    /// this is read by a person looking for the one number that is too big, not by an analysis that
    /// needs ticks.
    /// </summary>
    public IReadOnlyDictionary<string, long> Snapshot()
    {
        var snapshot = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in Order)
        {
            if (_elapsedMs.TryGetValue(name, out var ms))
            {
                snapshot[name] = (long)Math.Round(ms, MidpointRounding.AwayFromZero);
            }
        }

        return snapshot;
    }

    /// <summary>
    /// The one-line rendering used by the end-of-turn log. Invariant, though a structured-logging
    /// template would be too — the contract is stated here so a future non-template caller inherits
    /// it rather than a comma decimal separator.
    /// </summary>
    public override string ToString() =>
        string.Join(' ', Snapshot().Select(kv => string.Create(CultureInfo.InvariantCulture, $"{kv.Key}={kv.Value}ms")));

    private void Record(string name, double milliseconds) =>
        _elapsedMs[name] = _elapsedMs.GetValueOrDefault(name) + milliseconds;

    private sealed class Scope : IDisposable
    {
        private readonly AskStageTimings _owner;
        private readonly string _name;
        private readonly Activity? _activity;
        private readonly long _startedAt;
        private bool _disposed;

        public Scope(AskStageTimings owner, string name)
        {
            _owner = owner;
            _name = name;
            _activity = ActivitySource.StartActivity(name);
            _startedAt = Stopwatch.GetTimestamp();
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.Record(_name, Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds);
            _activity?.Dispose();
        }
    }
}
