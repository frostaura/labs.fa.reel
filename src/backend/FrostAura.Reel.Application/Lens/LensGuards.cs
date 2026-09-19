using System.Globalization;
using System.Text;
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

    /// <summary>
    /// PROSE named a title this account's library does not contain. Note the narrowing: an anchor
    /// entry that fails to resolve is stripped, not fatal — see <see cref="LensVerifier"/>.
    /// </summary>
    public const string InventedAnchor = "invented-anchor";

    public const string RestatedNumber = "restated-number";
    public const string UserReportedSpoiler = "user-reported-spoiler";

    /// <summary>
    /// <c>lens</c> or <c>hook</c> used a banned marketing adjective. Carries the word — the audit
    /// reads "marketing:unmissable" as a prompt defect, and "marketing" alone as a shrug.
    /// </summary>
    public const string Marketing = "marketing";

    /// <summary>
    /// The hook is a third-person tagline rather than something said to the viewer.
    ///
    /// <para><b>No token, because there is nothing to name.</b> Every other reason here points at an
    /// offending word; this one is the ABSENCE of the viewer from a line that exists to address
    /// them, and the evidence is the whole hook.</para>
    /// </summary>
    public const string HookVoice = "hook-voice";

    /// <summary>
    /// A reason plus the token that caused it — "invented-anchor:Frontera". Stored on the row so a
    /// prompt iteration can be read as evidence rather than as a tally: knowing that eleven notes
    /// were refused for an invented anchor is a number, and knowing that nine of them named the
    /// same declined title is a diagnosis.
    ///
    /// <para>Clipped to the column's 128 characters, token-side, so a pathological name truncates
    /// the evidence rather than the reason.</para>
    /// </summary>
    public static string WithToken(string reason, string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return reason;
        }

        const int columnWidth = 128;
        var room = columnWidth - reason.Length - 1;
        var trimmed = token.Trim();
        return room <= 0
            ? reason
            : reason + ":" + (trimmed.Length <= room ? trimmed : trimmed[..room]);
    }
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
///
/// <para><b>There is deliberately no deterministic POV-slip guard, and the reason is worth keeping.</b>
/// Prompt v4 added the rule that "you" is the viewer and never a character, after live copy read
/// "the city that failed you" about a city that failed HER. The obvious guard — a second-person
/// pronoun as the object of a verb inside a plot clause — cannot be written reliably at this cost.
/// "the city that failed you" and "the pacing that suited you" are the same shape, and only the
/// first is wrong; separating them needs to know that "the city" is a story entity, which this
/// class does not have and the regex cannot infer. A guard tuned to catch the first would refuse the
/// second, and honest second-person address is the entire product. So the slip is handled where it
/// can be judged rather than matched: the prompt states the rule with an example, and the audit's
/// <c>povSlips</c> note quotes every occurrence for the next prompt iteration to read.</para>
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

    /// <summary>
    /// The marketing register, as a closed list. Deliberately short and literal: these nine are the
    /// phrases prompt v4 bans by name, so the guard and the prompt can never disagree about what is
    /// forbidden. A long list of "salesy-sounding" words would refuse honest copy — "tense" and
    /// "relentless" are marketing words in the wrong sentence and accurate ones in the right sentence
    /// — and the judgement about a word that is only sometimes wrong belongs to the audit's
    /// <c>voice</c> score, not to a regex.
    ///
    /// <para>Applied to <c>lens</c> and <c>hook</c> only. <c>caution</c> is exempt on the same
    /// asymmetry that exempts it from the anti-recommendation rule: a reservation quoting the pitch
    /// it is arguing against — "it is not the gripping thing the trailer sold" — is doing its job.</para>
    /// </summary>
    [GeneratedRegex(
        @"\b(?:gripping|unmissable|masterful|stunning|unforgettable"
        + @"|must[\s\-]+see|thrilling\s+ride|edge[\s\-]+of[\s\-]+your[\s\-]+seat|tour\s+de\s+force)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex MarketingRegex();

    /// <summary>
    /// The viewer, addressed. "you", "your", "yours" and every contraction of them — <c>\b</c>
    /// closes "you'll" on the apostrophe, so the bare alternative covers the lot.
    /// </summary>
    [GeneratedRegex(@"\byou(?:rs?)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex SecondPersonRegex();

    /// <summary>
    /// The imperatives a hook may open on instead of naming the viewer — "Expect a slow hour",
    /// "Give this an evening if you have one". Small and closed on purpose: an open-ended
    /// "starts with a verb" test passes "Turns grief into a revenge chase", which is the exact
    /// poster line this guard exists to refuse.
    /// </summary>
    [GeneratedRegex(
        @"^[\s""'“‘]*(?:expect|give|watch|save|start|press|put|take|queue|try)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex HookImperativeRegex();

    /// <summary>
    /// Characters that may trail the last real word of a field: whitespace, and the punctuation a
    /// sentence can legitimately or accidentally end on.
    /// </summary>
    private static readonly char[] TailChars =
        [' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '…', '-', '–', '—'];

    /// <summary>The marks that actually close a sentence, as opposed to joining one to the next.</summary>
    private static readonly char[] Terminals = ['.', '!', '?', '…'];

    /// <summary>
    /// Trailing shapes that are already correct English and must be left exactly as they are.
    /// <c>"..."</c> is in here deliberately: a written-out ellipsis is a choice, not damage.
    /// </summary>
    private static readonly string[] CleanTails = [".", "!", "?", "?!", "!?", "…", "..."];

    /// <summary>
    /// How close to the requested cap a field has to sit before a missing full stop is read as a
    /// truncation rather than as a style. One character, because constrained decoding stops on the
    /// exact character and the only slack is a trailing space that <c>Trim</c> then removes.
    /// </summary>
    private const int CapSlack = 1;

    /// <summary>
    /// Repairs the two ways the response schema's <c>maxLength</c> damages copy, and nothing else.
    ///
    /// <para><b>One: a word cut in half.</b> A structured-output length limit is enforced by
    /// constrained decoding, so the string simply stops at the cap — live copy came back ending
    /// "…and a crowd-pleasing sw". A field sitting on its cap that does not close a sentence was
    /// cut, so the partial word goes and an ellipsis says so. The length gate is what keeps this
    /// off a hook that is merely a headline without a full stop; a short field that ends on a bare
    /// word is left alone, because there is no evidence anything was lost.</para>
    ///
    /// <para><b>Two: punctuation left stranded.</b> A model steering for a cap it can feel coming
    /// abandons a clause and closes anyway — "…the marriage strain,." — or trails a comma with
    /// nothing after it. The trailing run collapses to the last real terminal, or to an ellipsis
    /// when the run was all joiners and the sentence plainly went unfinished.</para>
    ///
    /// <para>Applied before the guards rather than after, so the deterministic rules judge the copy
    /// exactly as it will be stored and rendered. It is a repair, never a rewrite: a field that is
    /// already well-formed comes back reference-identical.</para>
    /// </summary>
    public static LensNote Tidy(LensNote note)
    {
        var lens = TidyField(note.Lens, LensNote.RequestLensChars);
        var hook = TidyField(note.Hook, LensNote.RequestHookChars);
        var caution = TidyCopy(note.Caution, LensNote.RequestCautionChars);

        return ReferenceEquals(lens, note.Lens)
            && ReferenceEquals(hook, note.Hook)
            && ReferenceEquals(caution, note.Caution)
            ? note
            : note with { Lens = lens, Hook = hook, Caution = caution };
    }

    /// <summary>
    /// One field. <paramref name="requestedMax"/> is the cap the PROMPT asked for, not the parse
    /// ceiling — it is the number the provider truncated against, so it is the number that says
    /// whether a truncation happened.
    /// </summary>
    public static string? TidyCopy(string? text, int requestedMax) =>
        text is null ? null : TidyField(text, requestedMax);

    /// <inheritdoc cref="TidyCopy"/>
    public static string TidyField(string text, int requestedMax)
    {
        var value = text.Trim();
        if (value.Length == 0)
        {
            return text;
        }

        if (value.Length >= requestedMax - CapSlack && !EndsOnATerminal(value))
        {
            var cut = value.LastIndexOf(' ');
            var body = (cut > 0 ? value[..cut] : value).TrimEnd(TailChars);

            // Everything before the last space was punctuation — there is no sentence left to
            // salvage, so the original stands rather than being reduced to an ellipsis.
            return body.Length == 0 ? text : body + "…";
        }

        var end = value.Length;
        while (end > 0 && TailChars.Contains(value[end - 1]))
        {
            end--;
        }

        if (end == value.Length)
        {
            return Same(text, value);
        }

        var prose = value[..end];
        var tail = new string(value[end..].Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (tail.Length == 0)
        {
            return Same(text, prose);
        }

        if (CleanTails.Contains(tail, StringComparer.Ordinal))
        {
            return Same(text, prose + tail);
        }

        var lastTerminal = tail.LastIndexOfAny(Terminals);
        return Same(text, lastTerminal >= 0 ? prose + tail[lastTerminal] : prose + "…");
    }

    /// <summary>Hands back the caller's own string when nothing actually changed, so
    /// <see cref="Tidy"/> can tell a repaired note from an untouched one by reference.</summary>
    private static string Same(string original, string candidate) =>
        string.Equals(original, candidate, StringComparison.Ordinal) ? original : candidate;

    /// <summary>
    /// Whether the field closes properly. A closing quote or bracket counts — it is the mark
    /// AFTER the full stop, not the absence of one.
    /// </summary>
    private static bool EndsOnATerminal(string value)
    {
        var last = value[^1];
        return Terminals.Contains(last) || last is '"' or '\'' or '’' or '”' or ')' or ']';
    }

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

        // The two register rules, LAST — this method is ordered worst-first so the reason on the row
        // is the most serious one, and a hook that both argues against its own card and forgets the
        // viewer should be filed under the first of those, not the second.
        if ((FirstMarketingWord(note.Lens) ?? FirstMarketingWord(note.Hook)) is { } marketing)
        {
            return LensRejectionReasons.WithToken(LensRejectionReasons.Marketing, marketing);
        }

        if (!IsAddressedToTheViewer(note.Hook))
        {
            return LensRejectionReasons.HookVoice;
        }

        return null;
    }

    /// <summary>
    /// The first banned marketing phrase in a field, normalised for the rejection token — collapsed
    /// whitespace, lowercased — or null. Normalising matters because the token is what the audit
    /// GROUPS by: "Must-See" and "must see" are one prompt defect and must count as one.
    /// </summary>
    internal static string? FirstMarketingWord(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = MarketingRegex().Match(text);
        return match.Success
            ? WhitespaceRegex().Replace(match.Value, " ").ToLowerInvariant()
            : null;
    }

    /// <summary>
    /// Whether the hook is said TO the viewer: it names them, or it opens on one of the imperatives
    /// aimed at them.
    ///
    /// <para><b>This one is a refusal and never a repair.</b> Every other guard here judges copy
    /// that could in principle be edited into shape; turning "Jodie Foster turns grief into a tight
    /// revenge chase" into a second-person hook means writing a different sentence, which is the
    /// model's job and not a guard's. So the note is rejected and the surface falls back, rather
    /// than rendering a poster line with a pronoun bolted on.</para>
    /// </summary>
    public static bool IsAddressedToTheViewer(string? hook) =>
        !string.IsNullOrWhiteSpace(hook)
        && (SecondPersonRegex().IsMatch(hook) || HookImperativeRegex().IsMatch(hook));

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
    /// The provenance verdict over the two PROSE findings: <paramref name="inventedPeople"/> are
    /// names the catalogue knows as real film people who are NOT in this title's credits, and
    /// <paramref name="inventedTitles"/> are titles the catalogue knows that this account's library
    /// does not contain, named in <c>lens</c>, <c>hook</c> or <c>caution</c>. Either is fatal, and
    /// both for the same reason: a fabricated connection to your own history is the most convincing
    /// kind of wrong, because it sounds like memory.
    ///
    /// <para><b>An unresolved ANCHOR entry is deliberately not here.</b> An anchor is a structured
    /// field the surface can simply not render, so a single unresolvable one is a reason to drop
    /// that entry — not a reason to throw away four sentences of otherwise sound prose. Refusing
    /// the whole note for it is what took the on-demand path to a hundred percent rejection.</para>
    /// </summary>
    public static string? InspectProvenance(
        IReadOnlyCollection<string> inventedPeople, IReadOnlyCollection<string> inventedTitles) =>
        inventedPeople.Count > 0
            ? LensRejectionReasons.WithToken(LensRejectionReasons.InventedPerson, inventedPeople.First())
            : inventedTitles.Count > 0
                ? LensRejectionReasons.WithToken(LensRejectionReasons.InventedAnchor, inventedTitles.First())
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

    /// <summary>
    /// Normalises a title for comparison: case, diacritics, punctuation, whitespace, a leading
    /// article, and a parenthesised trailing year.
    ///
    /// <para><b>Why this is not the "case and punctuation only" rule it replaces.</b> The model
    /// writes a title the way the sentence needs it — "Sicario (2015)", "The Amateur" for a
    /// library row called "Amateur", "Amelie" for "Amélie" — and every one of those is the same
    /// film. Comparing raw names calls all of them inventions, which is not a strict guard but a
    /// broken one: it refuses the truth at exactly the same rate it refuses a fabrication.</para>
    /// </summary>
    public static string NormaliseTitle(string title)
    {
        var stripped = title.Trim().Trim('"', '“', '”', '‘', '’', '\'');
        stripped = ParentheticalYearRegex().Replace(stripped, string.Empty);

        var folded = new StringBuilder(stripped.Length);
        foreach (var ch in stripped.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            folded.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        var collapsed = WhitespaceRegex().Replace(folded.ToString(), " ").Trim();
        return LeadingArticleRegex().Replace(collapsed, string.Empty);
    }

    /// <summary>
    /// The comparison keys for one title: its normalised form, plus — when the name ENDS in
    /// something that reads as a year — the same form with that year removed.
    ///
    /// <para><b>Why a second key rather than always stripping the year.</b> "Blade Runner 2049"
    /// and "1917" are titles whose names contain a year, and unconditional stripping turns the
    /// first into "Blade Runner", a different film. Emitting both keys keeps the exact name
    /// authoritative and lets the bare-year variant match only when nothing else does. The
    /// asymmetry of cost settles it: a false match keeps an anchor that perhaps should have gone,
    /// while a false miss deletes a true comparison to the viewer's own history.</para>
    /// </summary>
    public static IReadOnlyList<string> TitleKeys(string title)
    {
        var primary = NormaliseTitle(title);
        var withoutYear = TrailingYearRegex().Replace(primary, string.Empty).Trim();
        return withoutYear.Length > 0 && !string.Equals(withoutYear, primary, StringComparison.Ordinal)
            ? [primary, withoutYear]
            : [primary];
    }

    /// <summary>
    /// A title long enough that near-equality means something. Below this, two characters of edit
    /// distance is most of the word — "Up" and "Us" are one apart and unrelated.
    /// </summary>
    public const int MinFuzzyLength = 8;

    /// <summary>Edit distance tolerated on a long name. Covers a dropped article, a plural, a typo.</summary>
    public const int MaxEditDistance = 2;

    /// <summary>
    /// Does <paramref name="candidate"/> name something in <paramref name="library"/>? Exact on
    /// normalised keys first, then an edit-distance fallback for names long enough to survive one.
    /// <paramref name="library"/> is the key set built by <see cref="BuildTitleIndex"/>.
    /// </summary>
    public static bool MatchesLibrary(string candidate, IReadOnlyCollection<string> library)
    {
        var keys = TitleKeys(candidate);
        if (keys.Any(library.Contains))
        {
            return true;
        }

        foreach (var key in keys.Where(k => k.Length >= MinFuzzyLength))
        {
            foreach (var known in library.Where(k => k.Length >= MinFuzzyLength))
            {
                // Length alone rules most pairs out before any matrix is allocated, and the whole
                // library is scanned per anchor — at most three anchors per note.
                if (Math.Abs(known.Length - key.Length) <= MaxEditDistance
                    && EditDistanceWithin(key, known, MaxEditDistance))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Every comparison key for a set of known title names.</summary>
    public static HashSet<string> BuildTitleIndex(IEnumerable<string> names)
    {
        var index = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            foreach (var key in TitleKeys(name))
            {
                if (key.Length > 0)
                {
                    index.Add(key);
                }
            }
        }

        return index;
    }

    /// <summary>
    /// Levenshtein distance, abandoned as soon as it cannot come in at or under
    /// <paramref name="limit"/>. Two rows rather than a full matrix; the early exit is what keeps a
    /// per-anchor scan of a few thousand library keys cheap.
    /// </summary>
    internal static bool EditDistanceWithin(string a, string b, int limit)
    {
        if (Math.Abs(a.Length - b.Length) > limit)
        {
            return false;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            var best = current[0];
            for (var j = 1; j <= b.Length; j++)
            {
                var substitution = previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1);
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), substitution);
                best = Math.Min(best, current[j]);
            }

            if (best > limit)
            {
                return false;
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length] <= limit;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    /// <summary>A trailing "(2015)" or "[2015]" — how a model writes a disambiguating year.</summary>
    [GeneratedRegex(@"\s*[\(\[](?:19|20)\d{2}[\)\]]\s*$")]
    private static partial Regex ParentheticalYearRegex();

    /// <summary>A bare trailing year on an already-normalised name. Used for the SECONDARY key only.</summary>
    [GeneratedRegex(@"\s(?:19|20)\d{2}$")]
    private static partial Regex TrailingYearRegex();

    /// <summary>A leading article on an already-normalised name. "The Amateur" and "Amateur" are one film.</summary>
    [GeneratedRegex(@"^(?:the|a|an)\s")]
    private static partial Regex LeadingArticleRegex();
}
