using System.Text.RegularExpressions;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Application.Lens;

/// <summary>The named reasons a lens note can be refused. Persisted on the row and logged.</summary>
public static class LensRejectionReasons
{
    public const string Spoiler = "spoiler-lexicon";
    public const string ThirdPerson = "third-person-voice";
    public const string BannedOpener = "because-you-loved-opener";
    public const string AntiRecommendation = "anti-recommendation";
    public const string InventedPerson = "invented-person";
    public const string InventedAnchor = "invented-anchor";
    public const string RestatedNumber = "restated-number";
    public const string UserReportedSpoiler = "user-reported-spoiler";
}

/// <summary>
/// The output guards a generated lens must clear before anything renders it. Pure and static, so
/// every rule is unit-testable without a database — the provenance lookups that need one live in
/// <see cref="LensVerifier"/> and hand their results in here.
///
/// <para><b>The one asymmetry, and it is deliberate.</b>
/// <see cref="SecondPersonVoice.SoundsAntiRecommendation"/> is applied to <c>lens</c> and
/// <c>hook</c> — a pick surface must never caption its own recommendation with a reason to skip it
/// — and is NOT applied to <c>caution</c>. A reservation field that can never voice a reservation
/// is what makes copy read like marketing, and the honest downside is the whole trust argument for
/// this feature.</para>
///
/// <para><b><see cref="SecondPersonVoice.Rejects"/> is not used wholesale</b>, because its 220-char
/// runaway-length rule is calibrated for a ≤160-char why-line and a <c>lens</c> is allowed 420. The
/// two rules that do transfer — third-person leak and the banned opener — are applied field by
/// field, and length is enforced by the parser against each field's own limit.</para>
/// </summary>
public static partial class LensGuards
{
    /// <summary>
    /// Any digit sequence, or a spelled-out score. The prompt forbids restating the prediction;
    /// this is the check. "9/10", "8.4", "rated 7" and "a perfect ten out of ten" are all the same
    /// mistake — quoting a number the cache key deliberately does not contain, so a retrain would
    /// leave the prose stating a figure the product no longer believes.
    /// </summary>
    [GeneratedRegex(@"\d|\b(?:out of ten|out of 10|ten out of|percent)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NumberRegex();

    /// <summary>
    /// Candidate proper nouns: two or three consecutive capitalised words. Deliberately loose — it
    /// is only ever intersected against names the catalogue actually knows, so a false positive
    /// like "New York" costs nothing unless a person is really called that.
    /// </summary>
    [GeneratedRegex(@"\b[A-Z][\p{L}'’\-]+(?:\s+(?:van|von|de|del|della|di|da|der|den)\s+|\s+)[A-Z][\p{L}'’\-]+(?:\s+[A-Z][\p{L}'’\-]+)?")]
    private static partial Regex ProperNounRegex();

    /// <summary>Every prose field of a note, for the guards that apply to all of them.</summary>
    public static IEnumerable<string> ProseFields(LensNote note)
    {
        yield return note.Lens;
        yield return note.Hook;
        if (note.Caution is { Length: > 0 } caution)
        {
            yield return caution;
        }

        foreach (var anchor in note.Anchors)
        {
            yield return anchor.Relation;
        }
    }

    /// <summary>
    /// The guards that need nothing but the note itself. Returns the rejection reason, or null when
    /// the note is clean. Ordered worst-first so the reason recorded is the most serious one.
    ///
    /// <para><paramref name="titleName"/> and the note's own anchor titles are masked out before
    /// the number check runs. "Blade Runner 2049" and "1917" are titles, not scores, and a guard
    /// that cannot tell them apart would refuse every note about a film with a year in its name.</para>
    /// </summary>
    public static string? Inspect(LensNote note, string titleName)
    {
        if (SpoilerLexicon.FirstSpoiler(note) is not null)
        {
            return LensRejectionReasons.Spoiler;
        }

        var titles = new List<string> { titleName };
        titles.AddRange(note.Anchors.Select(a => a.Title));

        foreach (var field in ProseFields(note))
        {
            if (SecondPersonVoice.LeaksThirdPerson(field))
            {
                return LensRejectionReasons.ThirdPerson;
            }

            if (SecondPersonVoice.OpensWithBecauseYouLoved(field))
            {
                return LensRejectionReasons.BannedOpener;
            }

            if (NumberRegex().IsMatch(MaskTitles(field, titles)))
            {
                return LensRejectionReasons.RestatedNumber;
            }
        }

        // Applied to the two fields that SELL the pick, and never to the one that qualifies it.
        if (SecondPersonVoice.SoundsAntiRecommendation(note.Lens)
            || SecondPersonVoice.SoundsAntiRecommendation(note.Hook))
        {
            return LensRejectionReasons.AntiRecommendation;
        }

        return null;
    }

    /// <summary>
    /// Every proper noun the note mentions — the candidate set the provenance lookup resolves
    /// against the catalogue's own people.
    /// </summary>
    public static IReadOnlyCollection<string> CandidateNames(LensNote note)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in ProseFields(note))
        {
            foreach (Match match in ProperNounRegex().Matches(field))
            {
                names.Add(match.Value.Trim());
            }
        }

        return names;
    }

    /// <summary>
    /// The provenance verdict, given what the lookups found: <paramref name="inventedPeople"/> are
    /// names the catalogue knows as real film people who are NOT in this title's credits, and
    /// <paramref name="unratedAnchors"/> are anchor titles the account has never rated. Either is
    /// fatal, and both for the same reason: a fabricated connection to your own history is the most
    /// convincing kind of wrong, because it sounds like memory.
    /// </summary>
    public static string? InspectProvenance(
        IReadOnlyCollection<string> inventedPeople, IReadOnlyCollection<string> unratedAnchors) =>
        inventedPeople.Count > 0 ? LensRejectionReasons.InventedPerson
        : unratedAnchors.Count > 0 ? LensRejectionReasons.InventedAnchor
        : null;

    /// <summary>
    /// Blanks out every occurrence of a known title so the number check sees only the prose around
    /// it. Longest titles first, so masking "Blade Runner 2049" cannot be pre-empted by an anchor
    /// called "Blade Runner".
    /// </summary>
    internal static string MaskTitles(string field, IEnumerable<string> titles)
    {
        var masked = field;
        foreach (var title in titles
                     .Where(t => !string.IsNullOrWhiteSpace(t))
                     .Select(t => t.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(t => t.Length))
        {
            masked = masked.Replace(title, " ", StringComparison.OrdinalIgnoreCase);
        }

        return masked;
    }

    /// <summary>Normalises a title for comparison — case, whitespace and surrounding punctuation only.</summary>
    public static string NormaliseTitle(string title) =>
        WhitespaceRegex().Replace(title.Trim().Trim('"', '“', '”', '‘', '’', '\''), " ")
            .ToLowerInvariant();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
