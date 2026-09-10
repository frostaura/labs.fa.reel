namespace FrostAura.Reel.Application.Search;

/// <summary>
/// What the account's minimum-predicted-rating filter does to a turn's picks.
///
/// <para>The rule it replaces did three dishonest things at once. It compared against the floor
/// with <c>Count > 0</c>, so a turn where ONE card cleared a 6.0 bar returned that single card and
/// silently binned the rest. When nothing cleared, it kept the best six and said the floor had been
/// relaxed — but the six were a truncation nobody asked for, so "relaxed" also meant "and most of
/// your results were thrown away". And the outside-request channel skipped the floor entirely, so a
/// contradicting title the user had already excluded could appear beneath a bar it did not clear.</para>
///
/// <para>The rule now: a turn where fewer than <c>minKept</c> cards clear the floor RELAXES — the
/// floor is dropped for that turn and nothing is truncated. Otherwise the cards that clear are the
/// picks and the cards that do not are handed back as <see cref="Decision{T}.BelowFloor"/>, to be
/// shown in a tray and counted out loud. Either way the user is told what happened to every card,
/// which is the whole point: a drop the user cannot see is indistinguishable from a bug.</para>
///
/// <para>Generic over the card shape and pure, so the decision can be pinned exhaustively without a
/// database, a model or a turn.</para>
/// </summary>
public static class AskReelFloor
{
    /// <summary>
    /// The floor's verdict on one turn. <see cref="Kept"/> and <see cref="KeptOutside"/> keep their
    /// channels separate because outside cards always trail every clean pick on screen.
    /// </summary>
    public sealed record Decision<T>(
        IReadOnlyList<T> Kept,
        IReadOnlyList<T> KeptOutside,
        IReadOnlyList<T> BelowFloor,
        bool Relaxed)
    {
        /// <summary>How many cards the floor took off the screen. Zero on a relaxed turn, by construction.</summary>
        public int DroppedByFloor => BelowFloor.Count;
    }

    /// <summary>
    /// Applies <paramref name="floor"/> to both channels.
    ///
    /// <para>A card with NO prediction clears the floor. That is deliberate and it is the opposite
    /// call to the crowd-rating filter: a crowd rating is missing metadata about the title, whereas
    /// a missing prediction means this account has no model yet — refusing every card because we
    /// cannot score them is refusing to answer at all. The caller's job is to make sure the number
    /// is really absent before relying on this, which is why the null cards get a scoring pass
    /// immediately before the floor rather than after it.</para>
    ///
    /// <para><paramref name="minKept"/> is a THRESHOLD, not a take: it decides whether the floor
    /// binds this turn, and nothing is truncated to it in either branch.</para>
    /// </summary>
    public static Decision<T> Apply<T>(
        IReadOnlyList<T> clean,
        IReadOnlyList<T> outside,
        Func<T, decimal?> predictedOf,
        decimal floor,
        int minKept)
    {
        if (floor <= 0m)
        {
            return new Decision<T>(clean, outside, [], Relaxed: false);
        }

        bool Clears(T card) => predictedOf(card) is not { } predicted || predicted >= floor;

        var keptClean = clean.Where(Clears).ToList();
        var keptOutside = outside.Where(Clears).ToList();

        // Honouring the floor strictly would leave the screen too thin to be useful. Drop it for
        // this turn and SAY SO, rather than silently ignoring the setting or silently returning
        // almost nothing. Everything comes back — the old rule's Take(minKept) was a second,
        // unannounced cull layered on top of the first.
        if (keptClean.Count + keptOutside.Count < minKept)
        {
            return new Decision<T>(clean, outside, [], Relaxed: true);
        }

        var below = clean.Where(card => !Clears(card))
            .Concat(outside.Where(card => !Clears(card)))
            .ToList();
        return new Decision<T>(keptClean, keptOutside, below, Relaxed: false);
    }
}
