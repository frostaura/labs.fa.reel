using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Library;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ml;

/// <summary>
/// Resolves the personalisation context every LLM call in the product rides on: the account's
/// effective taste narrative — the user's own edit if they made one, otherwise the generated text
/// — falling back to the compact <see cref="TasteSummary"/> line for an account that has not been
/// written one yet.
///
/// <para>This is the single place the substitution happens, so the feed why pass, the paginator's
/// rerank and Ask Reel cannot end up describing the same person differently. Before it existed the
/// three surfaces each built their own twenty-token line and all three were wrong in the same two
/// ways.</para>
///
/// <para><b>The injected narrative is capped.</b> A user can edit their narrative to any length
/// they like, and it is prepended to every rerank prompt in the product — an unbounded field on a
/// per-batch prompt is a per-batch bill. The cap trims on a sentence boundary so the context ends
/// mid-thought as rarely as possible.</para>
/// </summary>
public sealed class PersonalisationContext(IReelDbContext db, DislikeSignal dislikes)
{
    /// <summary>
    /// The ceiling on the narrative injected into a prompt. ~1,200 characters is comfortably more
    /// than ten sentences and a hard stop on an edited field that is not.
    /// </summary>
    public const int MaxInjectedChars = 1200;

    /// <summary>
    /// The account's current narrative row, or null. Reads the newest version — regeneration
    /// writes a new row rather than mutating history, so the ordering is what picks the live one.
    /// </summary>
    public Task<AccountTasteNarrative?> CurrentAsync(Guid accountId, CancellationToken ct) =>
        db.AccountTasteNarratives
            .Where(n => n.AccountId == accountId)
            .OrderByDescending(n => n.Version)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// The taste text to hand a model: the effective narrative, capped, or the compact summary.
    /// Never empty — an account with nothing to say still gets "no strong signal yet", which is a
    /// true statement and a better prompt than an empty line.
    /// </summary>
    public async Task<string> ContextForAsync(
        Guid accountId, FeatureVectorBuilder.TasteState taste, CancellationToken ct)
    {
        var narrative = await CurrentAsync(accountId, ct);
        if (narrative is not null && !string.IsNullOrWhiteSpace(narrative.Effective))
        {
            return Cap(narrative.Effective);
        }

        return await TasteSummary.BuildAsync(db, accountId, taste, ct);
    }

    /// <summary>Loved anchor candidates named to a lens prompt. Enough to choose from, few enough to read.</summary>
    public const int LovedCandidates = 12;

    /// <summary>Disliked anchor candidates. Fewer, because a note may carry at most one honest negative.</summary>
    public const int DislikedCandidates = 6;

    /// <summary>Rated at or above this is a "loved" candidate — the bar the eval harness uses.</summary>
    private const short LovedFloor = 8;

    /// <summary>Rated at or below this is a "disliked" candidate.</summary>
    private const short DislikedCeiling = 4;

    /// <summary>
    /// The full persona — taste context, the recently-declined line, and the titles a lens may
    /// anchor against. The declined line was already reaching Ask Reel and had never reached the
    /// feed, so the feed's model had never been told a single thing the account dislikes.
    /// </summary>
    public async Task<LensPersona> PersonaForAsync(
        Guid accountId, FeatureVectorBuilder.TasteState taste, CancellationToken ct)
    {
        var taste_ = await ContextForAsync(accountId, taste, ct);
        var profile = await dislikes.GetProfileAsync(accountId, ct);
        var anchors = await AnchorCandidatesAsync(accountId, profile, ct);
        return new LensPersona(taste_, DislikeSignal.DeclinedLine(profile), anchors);
    }

    /// <summary>
    /// The persona as it stood at <paramref name="asOf"/> — the OFFLINE-ONLY sibling of
    /// <see cref="PersonaForAsync"/>, and the reason a cross-reference backtest can be believed.
    ///
    /// <para><b>Mirrors <c>EligibilityQueryBuilder.EligibleTitlesAsOf</c>, deliberately.</b> That
    /// method exists because the eligibility rule has to be re-askable at a past instant without
    /// present-day interactions leaking in; this one exists for exactly the same reason on the
    /// description side, and follows the same axis: the immutable <c>UserRating.FirstRatedAt</c>,
    /// never <c>RatedAt</c> and never the wall clock.</para>
    ///
    /// <para><b>It never reads the stored narrative.</b> <see cref="PersonaForAsync"/> prefers
    /// <c>AccountTasteNarratives.Effective</c>, which was written LAST WEEK from every rating the
    /// account has ever made — including the holdout ratings a backtest is about to score the judge
    /// against. Handing that text to the judge is not a subtle leak, it is the answer key. So the
    /// as-of persona is always built from <see cref="TasteSummary.BuildAsync"/> over the as-of taste
    /// state the caller passes in, and the caller is responsible for having built that state as-of
    /// too.</para>
    ///
    /// <para><b>The declined line and the anchors are cut at the same instant.</b> A reaction
    /// created after the split, or a rating first seen after it, would each hand the judge a fact
    /// from the future in a different costume. <c>CrossReferenceLeakageTests</c> asserts all three
    /// surfaces — persona text, anchors, neighbours — carry nothing newer than the split.</para>
    ///
    /// <para><b>Not for serving.</b> Nothing on a request path may call this: it is strictly more
    /// expensive and strictly less personal than the live persona, and its whole point is to be
    /// wrong about the present on purpose.</para>
    /// </summary>
    public async Task<LensPersona> PersonaAsOfAsync(
        Guid accountId, FeatureVectorBuilder.TasteState taste, DateTime asOf, CancellationToken ct)
    {
        var summary = await TasteSummary.BuildAsync(db, accountId, taste, ct);
        var anchors = await AnchorCandidatesAsOfAsync(accountId, asOf, ct);
        var declined = await DeclinedLineAsOfAsync(accountId, asOf, ct);
        return new LensPersona(summary, declined, anchors);
    }

    /// <summary>
    /// The anchor list as it stood at <paramref name="asOf"/>: the same ordering as
    /// <see cref="AnchorCandidatesAsync"/>, cut on <c>FirstRatedAt</c>.
    ///
    /// <para>Declined titles are deliberately absent here rather than filtered. The live anchor
    /// list draws them from <c>DislikeSignal.Profile</c>, which is a cached, recency-decayed
    /// aggregate with no as-of mode at all — re-deriving one would be a second implementation of a
    /// decay rule, and two implementations of a decay rule is how they drift. The backtest's
    /// persona therefore names only titles the account had rated by the split, which understates
    /// what the live judge sees and never overstates it.</para>
    /// </summary>
    internal async Task<IReadOnlyList<LensAnchorCandidate>> AnchorCandidatesAsOfAsync(
        Guid accountId, DateTime asOf, CancellationToken ct)
    {
        var rated = await db.UserRatings
            .Where(r => r.AccountId == accountId
                && r.FirstRatedAt <= asOf
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show)
                && (r.Rating >= LovedFloor || r.Rating <= DislikedCeiling))
            .Join(
                db.Titles,
                r => r.TitleId,
                t => t.Id,
                (r, t) => new { t.Name, t.Year, r.Rating, r.RatedAt })
            .Where(x => x.Name != null && x.Name != "")
            .ToListAsync(ct);

        var loved = rated
            .Where(x => x.Rating >= LovedFloor)
            .OrderByDescending(x => x.Rating)
            .ThenByDescending(x => x.RatedAt)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(LovedCandidates)
            .Select(x => new LensAnchorCandidate(x.Name, x.Year, x.Rating, AnchorStance.Loved));

        var disliked = rated
            .Where(x => x.Rating <= DislikedCeiling)
            .OrderBy(x => x.Rating)
            .ThenByDescending(x => x.RatedAt)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(DislikedCandidates)
            .Select(x => new LensAnchorCandidate(x.Name, x.Year, x.Rating, AnchorStance.Disliked));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return loved.Concat(disliked).Where(c => seen.Add(c.Name)).ToList();
    }

    /// <summary>
    /// The declined line as it stood at <paramref name="asOf"/>, straight off the reaction rows —
    /// created at or before the split and not revoked by then. Deliberately NOT
    /// <c>DislikeSignal.DeclinedLine</c>, whose profile is cached, decayed and always current.
    /// Null when the account had declined nothing yet, exactly as the live line is.
    /// </summary>
    internal async Task<string?> DeclinedLineAsOfAsync(Guid accountId, DateTime asOf, CancellationToken ct)
    {
        var declined = await db.UserTitleReactions
            .Where(r => r.AccountId == accountId
                && r.Kind == ReactionKind.NotInterested
                && r.CreatedAt <= asOf
                && (r.RevokedAt == null || r.RevokedAt > asOf))
            .OrderByDescending(r => r.CreatedAt)
            .Take(DislikeSignal.PromptCount)
            .Join(db.Titles, r => r.TitleId, t => t.Id, (r, t) => t.Name)
            .ToListAsync(ct);

        var names = declined.Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
        return names.Count == 0
            ? null
            : "Recently declined, do not recommend anything too similar: " + string.Join(", ", names);
    }

    /// <summary>
    /// The titles a lens note is permitted to name as an anchor: what this account loved, what it
    /// disliked, and what it declined — each with the year and the account's own verdict, so the
    /// model can write a true relation clause without guessing at one.
    ///
    /// <para><b>Declined titles are candidates on purpose.</b> They were already the only titles
    /// named anywhere in a lens prompt, by way of the declined line, and the model anchored on them
    /// because they were there. Listing them explicitly, with their stance, turns an accident into
    /// the intent: "you said this was not for you" is a legitimate and useful anchor, and it is now
    /// one the downstream check accepts.</para>
    ///
    /// <para>Ordered deterministically — rating, then recency, then name — so the same account
    /// produces the same prompt twice and a golden test means something.</para>
    /// </summary>
    public async Task<IReadOnlyList<LensAnchorCandidate>> AnchorCandidatesAsync(
        Guid accountId, DislikeSignal.Profile profile, CancellationToken ct)
    {
        var rated = await db.UserRatings
            .Where(r => r.AccountId == accountId
                && (r.SubjectType == RatingSubjectType.Movie || r.SubjectType == RatingSubjectType.Show)
                && (r.Rating >= LovedFloor || r.Rating <= DislikedCeiling))
            .Join(
                db.Titles,
                r => r.TitleId,
                t => t.Id,
                (r, t) => new { t.Name, t.Year, r.Rating, r.RatedAt })
            .Where(x => x.Name != null && x.Name != "")
            .ToListAsync(ct);

        var loved = rated
            .Where(x => x.Rating >= LovedFloor)
            .OrderByDescending(x => x.Rating)
            .ThenByDescending(x => x.RatedAt)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(LovedCandidates)
            .Select(x => new LensAnchorCandidate(x.Name, x.Year, x.Rating, AnchorStance.Loved));

        var disliked = rated
            .Where(x => x.Rating <= DislikedCeiling)
            .OrderBy(x => x.Rating)
            .ThenByDescending(x => x.RatedAt)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .Take(DislikedCandidates)
            .Select(x => new LensAnchorCandidate(x.Name, x.Year, x.Rating, AnchorStance.Disliked));

        var declinedIds = profile.Recent
            .Take(DislikeSignal.PromptCount)
            .Select(d => d.TitleId)
            .ToList();
        var years = declinedIds.Count == 0
            ? new Dictionary<Guid, int?>()
            : await db.Titles
                .Where(t => declinedIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Year, ct);
        var declined = profile.Recent
            .Take(DislikeSignal.PromptCount)
            .Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .Select(d => new LensAnchorCandidate(
                d.Name, years.GetValueOrDefault(d.TitleId), null, AnchorStance.Declined));

        // De-duplicated by name, first stance winning: a title that was rated AND later declined
        // is better described by the rating, which is the stronger statement.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return loved.Concat(disliked).Concat(declined)
            .Where(c => seen.Add(c.Name))
            .ToList();
    }

    /// <summary>Trims to <see cref="MaxInjectedChars"/> on the last sentence boundary that fits.</summary>
    internal static string Cap(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length <= MaxInjectedChars)
        {
            return trimmed;
        }

        var window = trimmed[..MaxInjectedChars];
        var stop = window.LastIndexOfAny(['.', '!', '?']);
        return stop > MaxInjectedChars / 2 ? window[..(stop + 1)] : window.TrimEnd() + "…";
    }
}
