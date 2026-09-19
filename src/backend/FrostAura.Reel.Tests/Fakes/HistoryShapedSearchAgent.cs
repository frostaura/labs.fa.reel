using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;

namespace FrostAura.Reel.Tests.Fakes;

/// <summary>
/// An <see cref="ISearchAgent"/> whose answer to a sentence depends on the CONVERSATION it is asked
/// in, which is what a real interpreter does and what no other stub in this suite reproduces.
///
/// <para>It exists so the interpretation cache can be probed without a language model. Every other
/// stub here answers a given sentence identically forever, so an interpretation served from the
/// wrong conversation's cache entry is indistinguishable from a correct one — the leak is
/// unobservable by construction. This one makes it observable: hand it a history in which somebody
/// said "only shows" and it narrows to <see cref="MediaType.Show"/> and says so; hand it the same
/// sentence with no history and it narrows to nothing and says something else. A reply carrying the
/// word "shows" in a thread that never asked for shows is therefore the leak itself, in the one
/// place the user actually sees it.</para>
///
/// <para><see cref="Calls"/> counts interpretations that genuinely reached the model, which is the
/// other half of the same question: the cache must still SPARE the model a byte-identical repeat
/// asked in a byte-identical context. A fix that simply made every turn a miss would pass a leak
/// assertion and lose the ~21 s → ~2.5 s repeat, so both facts are asserted against this counter.</para>
/// </summary>
internal sealed class HistoryShapedSearchAgent : ISearchAgent
{
    /// <summary>The sentence that, ANYWHERE in the history, makes this agent narrow to shows.</summary>
    internal const string ShowsOnlyMarker = "only shows";

    /// <summary>The reply a history that asked for shows produces — the leaked one, in the repro.</summary>
    internal const string ShowsReply = "Absolutely — I'll keep it to tense thriller shows and leave horror out.";

    /// <summary>The reply the identical sentence produces with no history behind it.</summary>
    internal const string UnshapedReply = "Absolutely — tense thrillers coming up, and I'll leave horror out.";

    private int _calls;

    /// <summary>How many interpretations reached the model rather than the cache.</summary>
    public int Calls => Volatile.Read(ref _calls);

    public bool IsAvailable => true;

    public string ModelId => "history-shaped-agent";

    public Task<AgentReply> InterpretAsync(
        IReadOnlyList<ChatTurn> history, string message, string tasteSummary, IReadOnlyList<string> shownTitles,
        CancellationToken ct = default, string? standingFilters = null)
    {
        Interlocked.Increment(ref _calls);
        var showsOnly = history.Any(t => t.Text.Contains(ShowsOnlyMarker, StringComparison.OrdinalIgnoreCase));
        var intent = new SearchIntent(
            Genres: ["thriller"],
            Keywords: ["tense"],
            MediaTypes: showsOnly ? [MediaType.Show] : [],
            FreeText: message,
            MinYear: null,
            MaxYear: null,
            ExcludeGenres: ["horror"]);
        return Task.FromResult(new AgentReply(showsOnly ? ShowsReply : UnshapedReply, intent, intent));
    }

    public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
        IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null,
        string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RerankResult>>(
            titles.Select(t => new RerankResult(t.TitleId, 0.7d, "Because it fits.")).ToList());
}
