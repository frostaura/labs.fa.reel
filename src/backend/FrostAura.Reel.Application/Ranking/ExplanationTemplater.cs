using System.Globalization;
using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Domain.Catalog;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// Deterministic why-this sentences from the model's actual top contributions — the honest
/// explainability layer behind every card (foresight DescriptionTemplater precedent; optional
/// LLM polish stays behind a flag, off by default). Never claims a reason the model didn't use.
/// </summary>
public static partial class ExplanationTemplater
{
    /// <summary>
    /// The RETIRED generic sentence. The templater wrote it whenever no clause survived, and it
    /// came back as 9.5% of a keyless build's why-lines — alone, and spliced after an anchor
    /// clause as "…, and Strong fit with your taste profile." (2026-10-26). It cites nothing, so
    /// the templater no longer writes it anywhere: nothing verifiable ⇒ no line, and an anchored
    /// card says its anchor link alone. Kept only so <see cref="FeedWhyGuard"/> can recognise a
    /// persisted copy, alone or as a tail, and refuse to serve it.
    /// </summary>
    public const string GenericFallback = "Strong fit with your taste profile.";

    /// <summary>A person the user has actually rated (count ≥ 2), with their shrunken affinity.</summary>
    public record PersonAffinity(string Name, decimal Affinity);

    /// <summary>Shared empty person map — a title with no user-rated cast/crew grounds no name clause.</summary>
    public static readonly IReadOnlyDictionary<CreditRole, PersonAffinity> NoPersons =
        new Dictionary<CreditRole, PersonAffinity>();

    /// <summary>
    /// Everything the sentence is allowed to cite, pre-verified by the caller so the templater can
    /// never fabricate a taste fact. <paramref name="VerifiedPersonByRole"/> holds only people the
    /// user has genuinely rated, keyed by their actual credit role (so a director clause never prints
    /// an actor's name); <paramref name="RatedGenres"/> holds only genres present in the user's
    /// GenreRatings (so a genre clause never names a genre the user never rated).
    /// <paramref name="AnchorSharedGenre"/> is a genre the title genuinely shares with the anchor,
    /// and <paramref name="Tone"/> the title's LLM-derived tone — printed ONLY when it is a member
    /// of the closed tone vocabulary, because rows extracted before vocabulary enforcement carry
    /// polluted free-text labels. <paramref name="AnchorEvidence"/> is what <see cref="AnchorRowGate"/>
    /// admitted the card on — a shared person, keyword or theme — and when present it is what the
    /// anchor clause names, ahead of the softer genre/tone link.
    /// <paramref name="AccountContrarianOffset"/> is the account's overall offset from the crowd —
    /// the number Taste DNA prints as its contrarian score — and null where the caller has no taste
    /// state: the contrarian clause then never fires, because "your tougher standards" is a claim
    /// about the reader that only that number can back.
    /// </summary>
    public record Context(
        Title Title,
        IReadOnlyList<(string Feature, float Contribution)> Contributions,
        IReadOnlyDictionary<CreditRole, PersonAffinity> VerifiedPersonByRole,
        IReadOnlySet<string> RatedGenres,
        string? AnchorName,
        string? AnchorSharedGenre = null,
        string? Tone = null,
        IReadOnlyList<SharedEvidence>? AnchorEvidence = null,
        decimal? AccountContrarianOffset = null);

    /// <summary>
    /// The contrarian score below which an account is harsher than the crowd: the lower edge of
    /// <see cref="Domain.Lens.ContrarianFact.Band"/>, the one band Taste DNA (<c>TasteDna.tsx</c>)
    /// and the taste narrative also read, so a card can never call the reader tougher than the
    /// crowd while their own DNA page says "with the crowd" — the founder, at −0.2, read exactly
    /// that contradiction on 19 of 21 Home cards (2026-10-25). Read from the band, not restated,
    /// so the three surfaces cannot drift apart (WS-1 CR-3).
    /// </summary>
    public const decimal HarsherThanCrowdBelow = -Domain.Lens.ContrarianFact.Band;

    /// <summary>
    /// The fewest crowd votes behind a number a why-line may quote — the supply reports' vote
    /// floor (<see cref="CrowdVotes.MinRelaxVotes"/>). Below it the shrunk score is mostly the
    /// catalogue prior, so "the crowd backs it" would be citing a crowd that is not there.
    /// </summary>
    public const int CrowdClauseMinVotes = CrowdVotes.MinRelaxVotes;

    /// <summary>
    /// The shrunk crowd score a clause may call praise ("the crowd backs it", "holds up"): half a
    /// point above the catalogue prior <see cref="CrowdRating.Prior"/> (6.5). At or below the
    /// prior the crowd is merely average about the title, and the clause would be filler.
    /// </summary>
    public const decimal CrowdPraiseFloor = 7.0m;

    /// <summary>
    /// The share of one rail's or one page's captions a single boilerplate phrase family may hold.
    /// A family that fits every card says nothing about any of them: the contrarian clause reached
    /// 116 of 150 why-lines once the era template was retired. The first card in a family is always
    /// allowed; after that, at most one card in four.
    /// </summary>
    public const double BoilerplateFamilyShareCap = 0.25d;

    /// <summary>
    /// How many of the card's largest POSITIVE contributions a clause may always cite. Together with
    /// <see cref="CitableContributionFloor"/> this is the one rule every clause obeys: a why-line
    /// names a reason the waterfall on the same title page shows as a reason. Before it, a card whose
    /// two biggest pushes were clause-less (release era +1.0, release-year fit +0.7) fell through to
    /// whatever came next — "in the same lane as the titles you love" on simTopLovedMean at +0.03
    /// (rank 4–5), "the crowd backs it too" on traktRating at +0.04 — on 15 of 15 Home cards, while the
    /// page below read "feels like your favourites +0.1, crowd rating +0.0" (2026-10-26).
    /// </summary>
    public const int CitableTopPositiveCount = 2;

    /// <summary>
    /// The contribution (in rating points) at which a push is big enough to cite whatever its rank:
    /// a feature moving the prediction by 0.15 or more is a reason a reader can see in the waterfall
    /// even behind two bigger ones. See <see cref="CitableTopPositiveCount"/>.
    /// </summary>
    public const float CitableContributionFloor = 0.15f;

    /// <summary>
    /// True when the contribution at <paramref name="index"/> may be cited by a clause: it is positive,
    /// and it is either among the card's <see cref="CitableTopPositiveCount"/> largest positive
    /// contributions or at least <see cref="CitableContributionFloor"/>. Ranked by value, not by list
    /// position, so it holds however the caller ordered the list (the engine orders by |value|, which
    /// interleaves negatives). Ties go to the earlier entry. Public so the surface harness can check a
    /// rendered line against the same rule the templater wrote it under.
    /// </summary>
    public static bool IsCitable(IReadOnlyList<(string Feature, float Contribution)> contributions, int index)
    {
        var value = contributions[index].Contribution;
        if (value <= 0)
        {
            return false;
        }

        if (value >= CitableContributionFloor)
        {
            return true;
        }

        var larger = 0;
        for (var j = 0; j < contributions.Count; j++)
        {
            var other = contributions[j].Contribution;
            if (j != index && other > 0 && (other > value || (other == value && j < index)))
            {
                larger++;
            }
        }

        return larger < CitableTopPositiveCount;
    }

    /// <summary>
    /// Builds the deterministic why-sentence. <paramref name="variantSeed"/> shifts every phrasing
    /// pick by that many variants — 0 is the title's canonical sentence; the no-repeat guard bumps
    /// it when two cards on one rail would otherwise render identical text.
    ///
    /// <para><b>Only the reasons that moved the number.</b> A clause cites a contribution only when
    /// <see cref="IsCitable"/> admits it — one of the card's two largest positive pushes, or one of at
    /// least <see cref="CitableContributionFloor"/> — so a line never names a reason the title page's
    /// waterfall shows as noise.</para>
    ///
    /// <para><b>Never a stub.</b> When no contribution yields a citable clause, an anchored card
    /// gets its anchor clause alone ("Written by Arthur Conan Doyle, like Sherlock.") and an
    /// unanchored card gets <see cref="string.Empty"/> — the card shows no line, which is the honest
    /// rendering of "nothing verifiable to say".</para>
    /// </summary>
    public static string BuildSentence(Context context, int variantSeed = 0) =>
        BuildSentence(context, variantSeed, NoSuppression);

    private static string BuildSentence(Context context, int variantSeed, IReadOnlySet<string> suppressed)
    {
        var clauses = new List<string>();

        for (var rank = 0; rank < context.Contributions.Count; rank++)
        {
            if (clauses.Count >= 2)
            {
                break;
            }

            var (feature, contribution) = context.Contributions[rank];
            if (suppressed.Contains(feature) || !IsCitable(context.Contributions, rank))
            {
                continue;
            }

            var clause = Translate(feature, contribution, rank, context, variantSeed);
            if (clause is not null && !clauses.Contains(clause))
            {
                clauses.Add(clause);
            }
        }

        if (context.AnchorName is { Length: > 0 } anchor)
        {
            // The anchor clause names what links the two titles (shared genre, tone) whenever
            // something verifiable is on hand — never the vague "Because you loved {anchor}"
            // template the founder rejected. With no reason clause behind it, it stands alone.
            clauses.Insert(0, AnchorClause(context, anchor, variantSeed));
        }

        if (clauses.Count == 0)
        {
            return string.Empty;
        }

        var sentence = string.Join(", and ", clauses.Take(2)).TrimEnd('.') + ".";

        // Sentence integrity: clause templates are written as mid-sentence fragments so they can
        // follow an anchor clause ("…, and you rate X 7.2 on average"). A fragment that ends up
        // LEADING the sentence must be promoted to sentence case — "you rate X 7.2 on average."
        // and "written by Stephen King, who you rate 6.8." shipped as whole whys (2026-08-10).
        return char.IsUpper(sentence[0]) ? sentence : char.ToUpperInvariant(sentence[0]) + sentence[1..];
    }

    /// <summary>
    /// The no-repeat guard for one rail: builds the title's sentence, and when an earlier card on
    /// the same rail already rendered exactly that text, retries with bumped variant seeds (max 3)
    /// until distinct. If every variant collides the last attempt is returned as-is — a repeated
    /// line is a flaw, a crash or a blank card would be a failure. Adds the result to
    /// <paramref name="takenWhys"/> either way.
    ///
    /// <para>It also holds the share cap (<see cref="BoilerplateFamilyShareCap"/>): a phrase family
    /// that already fills its share of what this rail or page shows is left out of this card, which
    /// then says the next true thing instead.</para>
    /// </summary>
    public static string BuildDistinctSentence(Context context, ISet<string> takenWhys)
    {
        var suppressed = new HashSet<string>(StringComparer.Ordinal);
        var sentence = Distinct(context, takenWhys, suppressed);

        // Each pass only ever suppresses more features, so this ends within one pass per family.
        while (true)
        {
            var newlySuppressed = false;
            foreach (var feature in FamiliesOverShare(sentence, takenWhys).SelectMany(f => f.Features))
            {
                newlySuppressed |= suppressed.Add(feature);
            }

            if (!newlySuppressed)
            {
                break;
            }

            sentence = Distinct(context, takenWhys, suppressed);
        }

        // An empty line is not something a later card can repeat, and it is not a line shown: it
        // stays out of the set the no-repeat guard and the family share count.
        if (sentence.Length > 0)
        {
            takenWhys.Add(sentence);
        }

        return sentence;
    }

    private static string Distinct(Context context, ISet<string> takenWhys, IReadOnlySet<string> suppressed)
    {
        var sentence = BuildSentence(context, 0, suppressed);
        for (var seed = 1; seed <= 3 && sentence.Length > 0 && takenWhys.Contains(sentence); seed++)
        {
            sentence = BuildSentence(context, seed, suppressed);
        }

        return sentence;
    }

    /// <summary>
    /// True when <paramref name="why"/> belongs to a capped phrase family (see
    /// <see cref="BoilerplateFamilyShareCap"/>) whose share the lines already in
    /// <paramref name="taken"/> fill — so this one would push it over. The denominator is the lines
    /// already shown plus this one; the first line of a family always fits.
    /// </summary>
    public static bool ExceedsFamilyShare(string? why, IEnumerable<string> taken) =>
        FamiliesOverShare(why, taken).Count > 0;

    /// <summary>True when <paramref name="why"/> carries the contrarian phrase family (current or retired phrasing).</summary>
    public static bool IsContrarianFamily(string? why) => Contrarian.Matches(why);

    private static List<PhraseFamily> FamiliesOverShare(string? why, IEnumerable<string> taken)
    {
        var mine = Families.Where(f => f.Matches(why)).ToList();
        if (mine.Count == 0)
        {
            return mine;
        }

        var shown = 0;
        var inFamily = new int[mine.Count];
        foreach (var line in taken)
        {
            shown++;
            for (var i = 0; i < mine.Count; i++)
            {
                if (mine[i].Matches(line))
                {
                    inFamily[i]++;
                }
            }
        }

        var allowed = Math.Max(1, (int)Math.Floor((shown + 1) * BoilerplateFamilyShareCap));
        return mine.Where((_, i) => inFamily[i] >= allowed).ToList();
    }

    /// <summary>
    /// A capped phrase family: how to recognise it in a rendered line, and the features whose
    /// clauses produce it (left out when the family is over its share).
    /// </summary>
    private sealed record PhraseFamily(Regex Pattern, IReadOnlyList<string> Features)
    {
        public bool Matches(string? why) => !string.IsNullOrEmpty(why) && Pattern.IsMatch(why);
    }

    /// <summary>
    /// The contrarian family: the clause written now ("…on your harsher-than-the-crowd scale",
    /// "…marked down to your scale") and the two phrasings retired on 2026-10-25 ("by your
    /// contrarian standards", "your tougher-than-the-crowd standards"), which reached 116 of 150 lines.
    /// </summary>
    private static readonly PhraseFamily Contrarian = new(ContrarianFamilyRegex(), ["contrarianAdjustedRating"]);

    /// <summary>
    /// The capped families. Only the contrarian one: the crowd clause ("the crowd backs it too (7.9
    /// on TMDB)") took its place on 46 of 96 rail cards in the 2026-10-25 slot measurement, but every
    /// one of those lines is true and quotes the badge, and capping it blanked 26 more cards (a
    /// capped card with no other true clause ships no line). Whether a true,
    /// badge-repeating line at that share is boilerplate is the surface harness's call to measure.
    /// </summary>
    private static readonly PhraseFamily[] Families = [Contrarian];

    private static readonly IReadOnlySet<string> NoSuppression = new HashSet<string>();

    [GeneratedRegex(@"\b(?:contrarian standards|tougher-than-the-crowd standards|harsher-than-the-crowd scale|marked down to your scale)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ContrarianFamilyRegex();

    /// <summary>
    /// The opening clause of an anchored card: the concrete link to the anchor. Prefers the richest
    /// verifiable combination — shared genre + closed-vocab tone, then tone, then genre — and only
    /// falls back to a bare same-lane phrasing when nothing specific is available to cite.
    /// </summary>
    private static string AnchorClause(Context context, string anchor, int seed)
    {
        var id = context.Title.Id;

        // The admitting evidence first: it is the one link on this card that is checkable — the
        // chip under the poster says the same thing in fewer words.
        if (context.AnchorEvidence is [var top, ..])
        {
            return (top.Kind, top.Role) switch
            {
                ("person", "director") => Pick(id, seed,
                    $"{top.Label} directs, as in {anchor}",
                    $"Same director as {anchor}: {top.Label}"),
                ("person", "writer") => Pick(id, seed,
                    $"Written by {top.Label}, like {anchor}",
                    $"Same writer as {anchor}: {top.Label}"),
                ("person", _) => Pick(id, seed,
                    $"{top.Label} again, as in {anchor}",
                    $"Shares {top.Label} with {anchor}"),
                ("keyword", _) => Pick(id, seed,
                    $"Shares the {top.Label} thread with {anchor}",
                    $"Like {anchor}, it turns on {top.Label}"),
                _ => Pick(id, seed,
                    $"Explores {top.Label}, like {anchor}",
                    $"Shares {anchor}'s theme of {top.Label}"),
            };
        }

        var tone = AttributeVocabulary.IsClosedVocabTone(context.Tone) ? context.Tone : null;
        var genre = context.AnchorSharedGenre is { Length: > 0 } g ? Pretty(g) : null;

        return (genre, tone) switch
        {
            ({ } sharedGenre, { } sharedTone) => Pick(id, seed,
                $"{IndefiniteArticle(sharedGenre, capitalised: true)} {sharedGenre} pick with the same {sharedTone} streak as {anchor}",
                $"Brings {anchor}'s {sharedTone} energy back to your {sharedGenre} lane",
                // "Brooding crime, just like Heat": the tone qualifies the genre. The old
                // "Like Heat, it plays crime brooding" put an adjective after its noun (R1-21).
                $"{Capitalised(sharedTone)} {sharedGenre}, just like {anchor}"),
            (null, { } sharedTone) => Pick(id, seed,
                $"Carries the same {sharedTone} streak as {anchor}",
                $"Shares {anchor}'s {sharedTone} tone",
                $"Runs on the {sharedTone} mood that made {anchor} land for you"),
            ({ } sharedGenre, null) => Pick(id, seed,
                $"{IndefiniteArticle(sharedGenre, capitalised: true)} {sharedGenre} pick in the same lane as {anchor}",
                $"Sits right beside {anchor} on the {sharedGenre} shelf",
                $"Scratches the {sharedGenre} itch {anchor} left you with"),
            _ => Pick(id, seed,
                $"Cut from the same cloth as {anchor}",
                $"In the same lane as {anchor}",
                $"A close cousin of {anchor}"),
        };
    }

    private static string? Translate(string feature, float contribution, int rank, Context context, int seed)
    {
        if (contribution <= 0)
        {
            return null; // only positive reasons belong on a recommendation card
        }

        var id = context.Title.Id;
        return feature switch
        {
            // Named person clauses fire ONLY when the contribution's role has a verified, user-rated
            // person — so a directorAffinity contribution can never print an actor's name, and no
            // clause ever names someone the user hasn't actually rated. When no verified person
            // exists for the role there is nothing honest to say, so the clause is skipped entirely
            // (fall through to the next contribution) rather than asserting an overlap or a "top
            // tier" the user cannot verify on the card.
            "castAffinity" or "lovedCastCount" when Verified(context, CreditRole.Actor) is { } actor =>
                Pick(id, seed,
                    string.Create(CultureInfo.InvariantCulture, $"you rate {actor.Name} {actor.Affinity:0.0} on average"),
                    string.Create(CultureInfo.InvariantCulture, $"{actor.Name} is in this — a {actor.Affinity:0.0} for you on average"),
                    string.Create(CultureInfo.InvariantCulture, $"stars {actor.Name}, whose work you rate {actor.Affinity:0.0}")),
            "directorAffinity" when Verified(context, CreditRole.Director) is { } director =>
                Pick(id, seed,
                    string.Create(CultureInfo.InvariantCulture, $"you rate {director.Name} {director.Affinity:0.0} on average"),
                    string.Create(CultureInfo.InvariantCulture, $"directed by {director.Name}, who you rate {director.Affinity:0.0}"),
                    string.Create(CultureInfo.InvariantCulture, $"{director.Name} directs — you rate them {director.Affinity:0.0}")),
            "writerAffinity" when Verified(context, CreditRole.Writer) is { } writer =>
                Pick(id, seed,
                    string.Create(CultureInfo.InvariantCulture, $"you rate {writer.Name} {writer.Affinity:0.0} on average"),
                    string.Create(CultureInfo.InvariantCulture, $"written by {writer.Name}, who you rate {writer.Affinity:0.0}")),
            "castAffinity" or "lovedCastCount" or "directorAffinity" or "writerAffinity" => null,
            "userGenreAffinity" when TopRatedGenre(context) is { } genre =>
                Pick(id, seed,
                    $"right in your {Pretty(genre)} wheelhouse",
                    $"{Pretty(genre)} is one of your strongest genres",
                    $"built for your {Pretty(genre)} taste"),
            "genreDrift" when TopRatedGenre(context) is { } genre =>
                Pick(id, seed,
                    $"matches the {Pretty(genre)} streak you've been on",
                    $"keeps your recent {Pretty(genre)} run going"),
            // decadeAffinity deliberately has NO clause. It is the model's top feature and a
            // survivorship artefact — older titles reached the user pre-selected by reputation — and
            // "a decade you rate highly" explained that artefact to the user as taste, on 60% of
            // cards. The feature stays in the model (removing it from ranking is an offline
            // question); the sentence is gone, and a null clause just moves on to the next one.
            // The crowd clause quotes the number the card's crowd badge shows — CrowdRating's
            // vote-shrunk, Trakt-first resolution — and names the source that number came from. It
            // fires only on a real crowd (≥ CrowdClauseMinVotes votes) that actually rates the title
            // above average; a 6.6 on 12 votes "backing" a pick is the prior talking.
            "traktRating" when PraisedCrowd(context.Title) is { } crowd =>
                Pick(id, seed,
                    string.Create(CultureInfo.InvariantCulture, $"the crowd backs it too ({crowd.Rating:0.0} on {crowd.Source})"),
                    string.Create(CultureInfo.InvariantCulture, $"rated {crowd.Rating:0.0} on {crowd.Source} by the crowd")),
            // tmdbPopularityLog deliberately has NO clause. It is a popularity LEVEL, and "trending
            // right now" is a claim about change: it captioned House (2004) and Law & Order: Criminal
            // Intent (2001). Nothing in the catalogue records popularity over time, so no recency
            // signal exists to make "trending" true; the feature stays in the model, the sentence goes.
            "releaseAgeLog" when IsFresh(context.Title) => Pick(id, seed, "fresh off release", "just released"),
            // The contrarian clause speaks about the READER, so it needs the reader's own number
            // behind it: an account genuinely harsher than the crowd (the Taste DNA threshold), a
            // contribution that is one of the two that actually moved this prediction (it was +0.1
            // against era's +1.0 on Master and Commander), and a real crowd whose score still reads
            // as praise once marked down by the reader's offset.
            "contrarianAdjustedRating" when rank < 2
                && context.AccountContrarianOffset is < HarsherThanCrowdBelow and { } offset
                && PraisedCrowd(context.Title) is { } crowd
                && crowd.Rating + offset is var adjusted && adjusted >= CrowdPraiseFloor =>
                Pick(id, seed,
                    // "an 8.0", "a 7.5": the article follows the number as it is read (R1-21).
                    $"holds {Rated(adjusted)} even on your harsher-than-the-crowd scale",
                    string.Create(CultureInfo.InvariantCulture, $"still {Rated(adjusted)} once the crowd's {crowd.Rating:0.0} is marked down to your scale")),
            "simLovedCentroid" or "simRecentCentroid" or "simTopLovedMax" or "simTopLovedMean" =>
                Pick(id, seed,
                    "close in feel to the titles you love",
                    "shares the feel of your favourites",
                    "in the same lane as the titles you love"),
            // A per-genre indicator clause names a genre only when the user has actually rated it.
            _ when feature.StartsWith("genre:") && context.RatedGenres.Contains(feature[6..]) =>
                Pick(id, seed, $"squarely {Pretty(feature[6..])}", $"firmly in {Pretty(feature[6..])} territory"),
            // Tone one-hots (schema v4), guarded by the same closed-vocabulary check AnchorClause
            // applies — a pre-enforcement row's free text must never reach the screen.
            _ when feature.StartsWith("tone:") && AttributeVocabulary.IsClosedVocabTone(feature[5..]) =>
                Pick(id, seed, $"{feature[5..]} in tone", $"has that {feature[5..]} streak"),
            // The era one-hots deliberately have NO clause yet. Half the vocabulary ("timeless",
            // "unknown", "contemporary") produces a sentence that says nothing, and an unexplained
            // feature costs nothing here: BuildSentence walks the whole contribution list, so a null
            // clause is skipped rather than blanking the card.
            _ => null,
        };
    }

    /// <summary>
    /// Deterministic phrasing pick per title, so a rail of cards doesn't repeat one sentence shape
    /// while the same title always renders the same sentence (stable across processes — no
    /// randomised string hashing involved). The seed walks the variants in order, so consecutive
    /// bumps from the no-repeat guard always reach a different phrasing when one exists.
    /// </summary>
    private static string Pick(Guid titleId, int seed, params string[] variants) =>
        variants[(int)((StableHash(titleId) + (uint)seed) % (uint)variants.Length)];

    private static uint StableHash(Guid titleId)
    {
        var bytes = titleId.ToByteArray();
        return (uint)(bytes[0] | (bytes[5] << 8) | (bytes[10] << 16) | (bytes[15] << 24));
    }

    /// <summary>
    /// The title's crowd score as its badge shows it (<see cref="CrowdRating.Resolve"/>) and the
    /// source it came from, or null when it is not a crowd a clause may praise: fewer than
    /// <see cref="CrowdClauseMinVotes"/> votes, or a score under <see cref="CrowdPraiseFloor"/>.
    /// </summary>
    private static (decimal Rating, string Source)? PraisedCrowd(Title title)
    {
        var (rating, votes) = CrowdRating.Resolve(title);
        if (rating is not { } score || votes is not { } count || count < CrowdClauseMinVotes || score < CrowdPraiseFloor)
        {
            return null;
        }

        return (score, CrowdRating.Source(title) == CrowdSource.Tmdb ? "TMDB" : "Trakt");
    }

    /// <summary>The verified, user-rated person for a role (null when none — no name is invented).</summary>
    private static PersonAffinity? Verified(Context context, CreditRole role) =>
        context.VerifiedPersonByRole.GetValueOrDefault(role);

    /// <summary>The title's first genre the user has actually rated (null when none is verifiable).</summary>
    private static string? TopRatedGenre(Context context) =>
        context.Title.Genres.FirstOrDefault(context.RatedGenres.Contains);

    private static bool IsFresh(Title title)
    {
        var released = title.ReleasedAt ?? title.FirstAiredAt;
        return released is { } date && (DateTime.UtcNow - date).TotalDays <= 120;
    }

    private static string Pretty(string slug) => slug.Replace('-', ' ');

    /// <summary>A rating with its article, as a sentence reads it: "an 8.0", "a 7.5", "a 10.0".</summary>
    private static string Rated(decimal rating)
    {
        var number = rating.ToString("0.0", CultureInfo.InvariantCulture);
        return $"{IndefiniteArticle(number)} {number}";
    }

    private static string Capitalised(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    /// <summary>
    /// "a" or "an" for <paramref name="word"/> — "an action pick", "a thriller", "an 8.0", "a 7.5".
    /// A word is judged by its first letter: every word this templater puts after an article is a
    /// genre slug from a closed vocabulary, and none of them starts with a silent consonant or a
    /// "you" sound. A number is judged by how it is read aloud (<see cref="NumberTakesAn"/>).
    /// </summary>
    public static string IndefiniteArticle(string word, bool capitalised = false)
    {
        var an = word.Length > 0 && (char.IsAsciiDigit(word[0]) ? NumberTakesAn(word) : "aeiouAEIOU".Contains(word[0]));
        return (an, capitalised) switch
        {
            (true, true) => "An",
            (true, false) => "an",
            (false, true) => "A",
            _ => "a",
        };
    }

    /// <summary>
    /// Whether a number written in digits is read with a vowel sound: everything read "eight…"
    /// (8, 8.0, 80, 800, 8,000), and "eleven…"/"eighteen…" when those lead a group of thousands
    /// (11, 18, 11,000, 18,500,000). "A 1.0", "a 7.5", "a 10.0" and "a 110" are consonant sounds.
    /// </summary>
    public static bool NumberTakesAn(string number)
    {
        var digits = new string(number.TakeWhile(c => char.IsAsciiDigit(c) || c == ',').Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0)
        {
            return false;
        }

        return digits[0] == '8'
            || (digits.Length % 3 == 2 && (digits.StartsWith("11", StringComparison.Ordinal) || digits.StartsWith("18", StringComparison.Ordinal)));
    }
}
