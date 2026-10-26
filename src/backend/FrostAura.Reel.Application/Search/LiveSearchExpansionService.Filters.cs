using System.Globalization;
using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Ports;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Search;

/// <summary>
/// v1's pool filters: the runtime ceiling, the relax ladder (its topic-dropping rung is a §4.13 deletion),
/// the bar, media and year, and the negation demerits. v1 only; deleted at cutover.
/// </summary>
public sealed partial class LiveSearchExpansionService
{
    /// <summary>
    /// The runtime post-filter for "under 90 minutes": titles with a KNOWN runtime over the
    /// ceiling are always dropped; unknown-runtime titles are kept only as a top-up to
    /// <paramref name="floor"/> when the known-and-fitting set alone cannot fill it — absence of
    /// a runtime is not evidence of fit, but an empty screen is the worse failure. No ceiling ⇒
    /// the pool passes through untouched. Internal so the tolerance rule is pinned by unit tests.
    /// </summary>
    internal static List<Title> ApplyRuntimeCeiling(List<Title> pool, int? maxRuntimeMinutes, int floor)
    {
        if (maxRuntimeMinutes is not { } ceiling)
        {
            return pool;
        }

        var fits = pool.Where(t => t.RuntimeMinutes is { } m && m <= ceiling).ToList();
        if (fits.Count >= floor)
        {
            return fits;
        }

        var unknown = pool.Where(t => t.RuntimeMinutes is null).Take(floor - fits.Count);
        return fits.Concat(unknown).ToList();
    }

    /// <summary>
    /// The richest concept text available for the semantic seed — the agent's reply plus the
    /// interpreted free-text, keywords, and genres. This is the "fantastically futuristic, tense,
    /// high-energy" phrasing the query embedding is built from; it embeds in the SAME vector space
    /// as the title embeddings (one <see cref="IEmbeddingProvider"/> by construction).
    /// </summary>
    /// <summary>
    /// One rung of the widening ladder. Level 0 is the intent as asked; each level after drops the
    /// narrowest surviving constraint, because an over-specified turn ("dark thriller serial killer
    /// that gets caught at the end") pulls almost nothing through TMDB's keyword AND-ing.
    /// Media type is never relaxed — it is the one constraint the user set explicitly.
    /// </summary>
    private static SearchIntent Relax(SearchIntent intent, int level) => level switch
    {
        0 => intent,
        // Keywords are AND-ed by TMDB discovery, so they collapse recall fastest — halve them.
        // With one keyword or none there is nothing to halve, so this rung would re-issue the
        // identical query and burn a TMDB round-trip widening nothing; drop the upper year bound
        // instead, which is the next-narrowest constraint after keywords.
        1 => intent.Keywords.Count > 1
            ? intent with { Keywords = intent.Keywords.Take(intent.Keywords.Count / 2).ToList() }
            : intent with { MaxYear = null },
        // Genre-only: the broadest pull that still honours the vibe.
        _ => intent with { Keywords = [], FreeText = string.Empty, MinYear = null, MaxYear = null },
    };

    /// <summary>
    /// The account's minimum-predicted-rating content filter, clamped to 0-10. Zero means "no floor
    /// set", which is also what a malformed stored value degrades to.
    ///
    /// <para>Reads rows the turn has ALREADY loaded rather than issuing its own query: the standing
    /// rules are needed before the interpret call anyway, and two reads of the same table one turn
    /// apart could disagree if the user edits their conditions mid-turn.</para>
    /// </summary>
    private static decimal MinPredictedFloor(IReadOnlyList<ContentFilter> rows)
    {
        var raw = rows.FirstOrDefault(f => f.Kind == FilterKind.MinPredictedRating)?.Value;
        return raw is not null && decimal.TryParse(raw, CultureInfo.InvariantCulture, out var value)
            ? Math.Clamp(value, 0m, 10m)
            : 0m;
    }

    /// <summary>
    /// The first instant of a release-year window, or null when unbounded — the single definition
    /// the concept fetch and the person-credit fetch both use. See <see cref="MinCalendarYear"/>
    /// for why the year is clamped rather than trusted.
    /// </summary>
    private static DateTime? YearStart(int? year) =>
        year is { } y ? new DateTime(ClampYear(y), 1, 1, 0, 0, 0, DateTimeKind.Utc) : null;

    /// <summary>
    /// The last instant of a release-year window, or null when unbounded. End-of-day, because
    /// <see cref="SearchIntent.MaxYear"/> is documented as inclusive of the whole year — the two
    /// call sites used to disagree about that by 24 hours.
    /// </summary>
    private static DateTime? YearEnd(int? year) =>
        year is { } y ? new DateTime(ClampYear(y), 12, 31, 23, 59, 59, DateTimeKind.Utc) : null;

    /// <inheritdoc cref="MinCalendarYear"/>
    private static int ClampYear(int year) => Math.Clamp(year, MinCalendarYear, MaxCalendarYear);

    /// <summary>True when the title matches the requested media types (empty list = no constraint).</summary>
    private static bool MatchesMedia(Title title, IReadOnlyList<MediaType> mediaTypes) =>
        mediaTypes.Count == 0 || mediaTypes.Contains(title.MediaType);

    /// <summary>
    /// True when the title falls inside the requested release-year window (null bounds = no
    /// constraint). Applied to the ELIGIBLE pool, not just to TMDB discovery, because discovery is
    /// only one of two arms: the semantic kNN arm ranks the account's own embedded catalogue by
    /// cosine and knows nothing about years, so "80s sci-fi" still surfaced 2026 titles underneath
    /// five genuinely-80s ones. A title with no known date is kept rather than dropped — absence of
    /// a date is not evidence of the wrong date, and the relaxation ladder drops these bounds
    /// anyway if the window turns out to be too narrow to fill a screen.
    /// </summary>
    private static bool MatchesYear(Title title, SearchIntent intent)
    {
        if (intent.MinYear is null && intent.MaxYear is null)
        {
            return true;
        }

        var year = title.Year ?? (title.ReleasedAt ?? title.FirstAiredAt)?.Year;
        if (year is null)
        {
            return true;
        }

        return (intent.MinYear is not { } lo || year >= lo)
            && (intent.MaxYear is not { } hi || year <= hi);
    }

    /// <summary>
    /// How much a title contradicts what the user's own words ruled out. Each matched genre or
    /// keyword costs one <see cref="NegationPenalty"/>. Any non-zero value routes the title into
    /// the flagged outside-request partition (<see cref="PartitionByNegation"/>) — it never reaches
    /// the screen as an ordinary pick.
    ///
    /// <para>It reads ONLY the requested half of <see cref="AskReelNegations"/>. The standing half
    /// is not a demotion at all: a title tripping one is culled before this runs, because an
    /// account-level exclusion is airtight and "shown, but flagged" is still shown.</para>
    /// </summary>
    private static double RequestedDemerit(Title title, AskReelNegations negations)
    {
        var hits = negations.RequestedGenres.Count(bad => TripsExcludedGenre(title, bad));
        hits += negations.RequestedKeywords.Count(w => w.Length > 2 && TripsKeyword(title, w));

        return Math.Min(hits * NegationPenalty, 0.66);
    }

    /// <summary>
    /// Whether a title trips one of the account's PERMANENT exclusions, under exactly the matching
    /// <see cref="TripsExcludedGenre"/> uses.
    ///
    /// <para><b>Why this exists on top of the eligibility query.</b>
    /// <c>EligibilityQueryBuilder</c> excludes a genre with <c>t.Genres.Contains(value)</c> — an
    /// exact match against the genre array, and nothing else. This matcher is deliberately richer:
    /// it also reads <c>Subgenres</c> and <c>Keywords</c> by containment, because TMDB's TV taxonomy
    /// has no Horror genre at all and an exclusion blind to those is blind to TV horror. The two
    /// therefore disagree by construction, and the gap is real on screen: a title whose
    /// <c>Genres</c> says drama and whose keywords say "black comedy" passes the SQL filter for an
    /// account that permanently excludes comedy, and then gets captioned by this pipeline as "a
    /// comedy title". It reached the outside channel under a notice stating that comedy can never
    /// appear.</para>
    ///
    /// <para>Closing it HERE rather than in the SQL is deliberate. The eligibility rule is
    /// founder-locked and its query is read by every surface in the product; widening it to
    /// keyword containment would quietly re-cut the feed, the rails and the digest on the strength
    /// of a defect found in one channel of one surface. The partition is where this turn's screen
    /// is decided, and it is the last place a standing exclusion can still be honoured.</para>
    /// </summary>
    private static bool TripsStandingExclusion(Title title, AskReelNegations negations)
    {
        if (negations.StandingGenres.Any(bad => TripsExcludedGenre(title, bad)))
        {
            return true;
        }

        return negations.StandingKeywords.Any(w => w.Length > 2 && TripsKeyword(title, w));
    }

    /// <summary>
    /// Whether a title trips one excluded genre term. TMDB's TV taxonomy has no "Horror" genre at
    /// all (American Horror Story arrives as drama/mystery/sci-fi), so an exclusion matched only
    /// against <see cref="Title.Genres"/> is blind to TV horror — the exact gap the 2026-08-10
    /// live replay caught. Subgenres and Keywords carry the truth ("anthology horror",
    /// "supernatural horror", "horror"), so the excluded term also matches those labels.
    ///
    /// <para>Matched through <see cref="NegationTerms"/>: any singular or plural form, as a WHOLE
    /// phrase inside a label — "horror" still trips "supernatural horror", and "music" no longer
    /// trips "musical" (M NEW-3).</para>
    /// </summary>
    private static bool TripsExcludedGenre(Title title, string excluded) =>
        (title.Genres is { Length: > 0 } && title.Genres.Any(g => NegationTerms.EqualsAnyForm(g, excluded)))
        || NegationTerms.AnyLabelMatches(title.Subgenres, excluded)
        || NegationTerms.AnyLabelMatches(title.Keywords, excluded);

    /// <summary>
    /// Whether a negated WORD trips a title: any form of it, as a whole phrase, in the display text,
    /// the TMDB keyword and subgenre labels, or as one of its genres — "no superheroes" trips the
    /// singular label <c>superhero</c> and the <c>superhero</c> genre alike (A-9).
    /// </summary>
    private static bool TripsKeyword(Title title, string word) =>
        (title.Genres is { Length: > 0 } && title.Genres.Any(g => NegationTerms.EqualsAnyForm(g, word)))
        || NegationTerms.AnyLabelMatches(title.Keywords, word)
        || NegationTerms.AnyLabelMatches(title.Subgenres, word)
        || NegationTerms.MatchesPhrase(title.Name, word, literalOnly: true)
        || NegationTerms.MatchesPhrase(title.Overview, word, literalOnly: true);
}
