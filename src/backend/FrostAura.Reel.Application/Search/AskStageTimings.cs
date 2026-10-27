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

    /// <summary>Inside <see cref="Discover"/>: WS-3's TMDB arm (or the person-credit pull), deadline included.</summary>
    public const string DiscoverTmdb = "discover.tmdb";

    /// <summary>Inside <see cref="Discover"/>: the facet arm and the supply report behind it.</summary>
    public const string DiscoverFacet = "discover.facet";

    /// <summary>Inside <see cref="Discover"/>: the semantic kNN arm, including any wait on its embedding.</summary>
    public const string DiscoverKnn = "discover.knn";

    /// <summary>
    /// Inside <see cref="Embed"/>: embedding the eligible titles that had no vector yet — newly discovered
    /// ones, mostly. Absent when every eligible title was already embedded.
    /// </summary>
    public const string EmbedTitles = "embed.titles";

    /// <summary>
    /// Inside <see cref="Embed"/>: waiting on the ranking seed — the concept embedding started before
    /// discovery. Near zero when it finished underneath the arms; the remainder of an uncached
    /// embedding when it did not, which §4.11's retrieve budget does not allow for.
    /// </summary>
    public const string EmbedSeed = "embed.seed";

    /// <summary>
    /// Inside <see cref="Score"/>: hydrating the shortlist's never-refreshed titles from TMDB. Absent
    /// when every title on the shortlist was already hydrated.
    /// </summary>
    public const string ScoreHydrate = "score.hydrate";

    /// <summary>Inside <see cref="Rerank"/>: from the start of judging to the first batch returning.</summary>
    public const string RerankFirstBatch = "rerank.first";

    /// <summary>From the start of the turn to the first pick on the wire — §4.11's first-card number.</summary>
    public const string FirstCard = "firstCard";

    /// <summary>Ask Reel v2: the planner call (§4.2 step 1).</summary>
    public const string Plan = "plan";

    /// <summary>Ask Reel v2: building the planner's context (taste summary, persona), inside <see cref="Plan"/>.</summary>
    public const string PlanContext = "plan.context";

    /// <summary>Ask Reel v2: the account's conditions and the conversation's state, read before the plan.</summary>
    public const string Load = "load";

    /// <summary>
    /// Ask Reel v2: the retrieval tool (§4.2 step 2) — on a turn whose plan matched the speculative
    /// reading, only the part of it still running once the plan returned.
    /// </summary>
    public const string Retrieve = "retrieve";

    /// <summary>
    /// Ask Reel v2: the speculative retrieval's own length, run BESIDE the plan on the message's
    /// keyless reading and used only when the plan asked for exactly the same query (HREV1-ASK-05).
    /// Present only on a turn that used it.
    /// </summary>
    public const string RetrieveSpeculative = "retrieve.spec";

    /// <summary>Ask Reel v2: loading the retrieved titles, after retrieval and before judging.</summary>
    public const string RetrieveTitles = "retrieve.titles";

    /// <summary>Ask Reel v2: the judge's inputs (fact rows, persona) read before its first batch is sent.</summary>
    public const string JudgePrep = "judge.prep";

    /// <summary>v2: a mood request's text embedded ahead of retrieval, so the semantic arm has it (see AskReelEngine.WarmMoodAsync).</summary>
    public const string RetrieveEmbed = "retrieve.embed";

    /// <summary>Ask Reel v2: every judge batch, first sent to last returned (§4.2 step 3).</summary>
    public const string Judge = "judge";

    /// <summary>Ask Reel v2: the first judge batch to return (of the catalogue pass).</summary>
    public const string JudgeFirstBatch = "judge.first";

    /// <summary>
    /// Ask Reel v2: the early first judge batch's model call, run beside the plan on the speculative
    /// retrieval (HREV1-ASK-05). Present only on a turn that took that retrieval and whose early call
    /// came back; its verdicts are the ones used wherever the plan asked the same request.
    /// </summary>
    public const string JudgeSpeculative = "judge.spec";

    /// <summary>Ask Reel v2: the ItemQuestion answer call.</summary>
    public const string Answer = "answer";

    /// <summary>Ask Reel v2: composing the reply and the terminal frame.</summary>
    public const string Compose = "compose";

    /// <summary>
    /// Canonical order — the order the work happens in, which is the order a reader wants it. The
    /// snapshot is built from this list, so a stage that never ran is simply absent rather than zero.
    /// The dotted entries are breakdowns of the undotted stage above them and are not added to it again.
    /// </summary>
    public static readonly IReadOnlyList<string> Order =
        [
            Interpret, Discover, DiscoverTmdb, DiscoverFacet, DiscoverKnn, Eligible, Embed, EmbedTitles, EmbedSeed,
            Score, ScoreHydrate, Rerank, RerankFirstBatch, Blend, Emit, FirstCard,
            Load, Plan, PlanContext, RetrieveEmbed, Retrieve, RetrieveSpeculative, RetrieveTitles, JudgePrep, Judge,
            JudgeFirstBatch, JudgeSpeculative, Answer, Compose,
        ];

    /// <summary>
    /// The pipeline's own stages — sequential and disjoint, so they sum to no more than the turn. The
    /// rest of <see cref="Order"/> are breakdowns of one of these, or a point in time.
    /// </summary>
    public static readonly IReadOnlyList<string> Sequential =
        [Interpret, Discover, Eligible, Embed, Score, Rerank, Blend, Emit];

    private readonly Dictionary<string, double> _elapsedMs = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cached = new(StringComparer.Ordinal);

    /// <summary>
    /// Records that a stage was answered from cache rather than done. A zero beside a stage that
    /// normally costs seconds is the single most misread number in this log line — it looks like an
    /// instrumentation bug, so the reader ends up distrusting the rest of the timings. Saying
    /// <c>interpret=0ms(cached)</c> makes the zero self-explanatory, and makes hit rate readable
    /// from the logs without a second counter to keep in step with this one.
    /// </summary>
    public void MarkCached(string name) => _cached.Add(name);

    /// <summary>
    /// Times everything until the returned handle is disposed, adding it to <paramref name="name"/>'s
    /// running total. Use as <c>using var _ = timings.Stage(AskStageTimings.Embed);</c>.
    /// </summary>
    public IDisposable Stage(string name) => new Scope(this, name);

    /// <summary>
    /// Records a duration measured elsewhere — a point in time rather than a span this class wraps
    /// (<see cref="FirstCard"/>, <see cref="RerankFirstBatch"/>). Accumulates like a stage.
    /// </summary>
    public void Mark(string name, double milliseconds) => Record(name, milliseconds);

    /// <summary>Whether <paramref name="name"/> has been recorded on this turn.</summary>
    public bool Has(string name) => _elapsedMs.ContainsKey(name);

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
        string.Join(' ', Snapshot().Select(kv => string.Create(
            CultureInfo.InvariantCulture,
            $"{kv.Key}={kv.Value}ms{(_cached.Contains(kv.Key) ? "(cached)" : string.Empty)}")));

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
