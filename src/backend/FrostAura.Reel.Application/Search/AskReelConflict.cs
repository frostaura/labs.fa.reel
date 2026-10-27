namespace FrostAura.Reel.Application.Search;

/// <summary>
/// The collision notice, as a value — stored on the turn and handed back on every read route.
///
/// <para><b>Why it is a row and not just a frame.</b> The notice is the whole account of why an
/// answer looks nothing like the question. It was stream-only: ask for a 90s romantic comedy against
/// standing exclusions on both genres, reload, and the thread comes back as four post-2000 dramas
/// under that question with nothing left on the page saying why. The cards are restored from rows
/// and the reason line from a column; this was the one part of the turn the server forgot, and it is
/// the part the user most needs when the answer surprises them.</para>
///
/// <para><b>Stored rather than recomputed.</b> The account's conditions can change between the turn
/// and the resume — that is the entire point of the Conditions screen the notice tells them to open
/// — so a notice re-derived from today's rules would describe an answer that was produced under
/// yesterday's. The transcript records what the user was told, for the same reason
/// <c>SearchTurn.Reason</c> does.</para>
///
/// <para><b>What is deliberately absent: <c>poolSize</c> and <c>catalogueSize</c>.</b> Both are
/// counts of the catalogue at one instant. They are honest on the wire, where the instant is now,
/// and they stop being true the moment the next title is ingested — so a resume that replayed them
/// would be stating a stale number as a current one, which is the failure this whole record exists
/// to avoid. The live frame carries them; the row does not.</para>
/// </summary>
/// <param name="ExcludedGenres">Genres the turn asked for that the account's conditions exclude.</param>
/// <param name="ExcludedKeywords">Words the turn asked for that the account's conditions avoid.</param>
/// <param name="MinYear">
/// The account's standing lower year bound — present ONLY when the year is what collided. An account
/// with a year window has one on every turn, so storing it unconditionally would put a year chip on
/// a screen whose collision was purely a genre.
/// </param>
/// <param name="MaxYear">The standing upper bound, on the same terms as <paramref name="MinYear"/>.</param>
/// <param name="Message">
/// The prose the user was shown, verbatim. Every number a client renders travels beside it as raw
/// JSON so the copy stays the client's to shape — the two year bounds inside this sentence are the
/// exception, because they are part of the sentence and are invariant like every other number this
/// backend formats.
/// </param>
public sealed record AskReelConflict(
    IReadOnlyList<string> ExcludedGenres,
    IReadOnlyList<string> ExcludedKeywords,
    int? MinYear,
    int? MaxYear,
    string Message);
