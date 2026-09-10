namespace FrostAura.Reel.Application.Search;

/// <summary>
/// The ceiling an Ask Reel request has to clear BEFORE anything is spent on it — before the
/// interpret call, before the TMDB pull, before a turn is persisted.
///
/// <para>It exists because every one of those costs is unbounded in the request: the query goes
/// straight into a prompt, the history goes in behind it, and the shown-ids list becomes an
/// <c>IN (…)</c> over the catalogue. A client bug that appends instead of replacing, or a paste of
/// a whole document, therefore bought a full-price turn with no upper bound on the prompt it
/// built. The transport-level body cap catches the extreme; this catches the shapes that fit
/// comfortably inside 64 KB and are still nonsense.</para>
///
/// <para><b>A refusal is not a failure.</b> Nothing broke, so the terminal frame carries
/// <c>failed:false</c> and prose the person can act on — the client's crash copy is reserved for
/// an actual crash, and conflating the two is what taught the UI to string-match reasons.</para>
///
/// <para>Pure and static so the bounds can be pinned without a host: the endpoint's whole
/// responsibility is to call <see cref="Refuse"/>, emit <see cref="RefusalPayload"/> and return.</para>
/// </summary>
public static class AskRequestLimits
{
    /// <summary>Longest question accepted. Comfortably past any real turn; far short of a document.</summary>
    public const int MaxQueryChars = 1000;

    /// <summary>How many prior turns may ride along. The agent prompt only reads the recent tail anyway.</summary>
    public const int MaxHistoryTurns = 12;

    /// <summary>Longest single history turn accepted — the same bound as a live question.</summary>
    public const int MaxHistoryTurnChars = 1000;

    /// <summary>How many already-shown TMDB ids may ride along, before the exclusion set is the query.</summary>
    public const int MaxShownIds = 300;

    /// <summary>
    /// Transport-level body cap for <c>/api/search/ask</c>, in bytes. Sits above every field bound
    /// combined, so a request that trips THIS was never a plausible turn — it is caught by Kestrel
    /// before a byte reaches the handler, which is the only way to bound work the handler never
    /// gets to inspect.
    /// </summary>
    public const long MaxBodyBytes = 65536;

    /// <summary>
    /// Why this request is refused before any spend, or <c>null</c> when it is within bounds.
    /// <paramref name="historyTexts"/> is the raw turn text in request order; nulls and blanks
    /// count towards neither bound, because the endpoint drops them anyway.
    /// </summary>
    public static string? Refuse(string? query, IReadOnlyList<string?>? historyTexts, int shownCount)
    {
        if ((query ?? string.Empty).Length > MaxQueryChars)
        {
            return "That question is longer than I can work with — try trimming it to a sentence or two.";
        }

        var turns = (historyTexts ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        if (turns.Count > MaxHistoryTurns)
        {
            return "This thread has grown too long to carry forward — start a fresh one and I'll pick it up.";
        }

        if (turns.Any(t => t!.Length > MaxHistoryTurnChars))
        {
            return "One of the earlier messages in this thread is too long to carry forward — start a fresh one.";
        }

        return shownCount > MaxShownIds
            ? "You've seen a lot this session — start a fresh thread and I'll look again with clear eyes."
            : null;
    }

    /// <summary>
    /// The terminal frame a refused request answers with: no results, no crash, and the reason as
    /// prose. Shaped here rather than at the endpoint so the <c>failed:false</c> half cannot drift.
    /// </summary>
    public static object RefusalPayload(string reason) => new
    {
        results = Array.Empty<object>(),
        reason,
        failed = false,
    };
}
