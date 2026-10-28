using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Search.AskReel.Engine;

/// <summary>
/// The catalogue facts Ask Reel v2 reads (§4.4 <c>title_facts</c>, and the judge's per-title input
/// in §4.5): keywords, genres, a short overview, credits, runtime, certification, the trusted
/// themes and tone, and whether the account has seen the title. Lookups only — nothing here is a
/// suggestion, so nothing here is filtered by eligibility.
/// </summary>
public sealed class TitleFacts(IReelDbContext db) : ITitleFacts
{
    /// <summary>How many keywords the judge sees per title (§4.5: the top 12).</summary>
    public const int JudgeKeywords = 12;

    /// <summary>How much of the overview the judge sees (§4.5: 200 characters).</summary>
    public const int JudgeOverviewChars = 200;

    /// <summary>How many cast names a fact sheet carries.</summary>
    public const int FactCast = 6;

    public async Task<TitleFactSheet?> GetAsync(Guid accountId, Guid titleId, CancellationToken ct)
    {
        var title = await db.Titles.AsNoTracking().FirstOrDefaultAsync(t => t.Id == titleId, ct);
        if (title is null)
        {
            return null;
        }

        var attributes = await TrustedAttributesAsync([titleId], ct);
        var credits = await db.TitleCredits
            .Where(c => c.TitleId == titleId)
            .Join(db.Persons, c => c.PersonId, p => p.Id, (c, p) => new { c.Role, c.CastOrder, p.Name })
            .ToListAsync(ct);
        var seen = await db.WatchedTitles.AnyAsync(w => w.AccountId == accountId && w.TitleId == titleId, ct)
            || await db.UserRatings.AnyAsync(r => r.AccountId == accountId && r.TitleId == titleId, ct);
        var hasAttributes = attributes.TryGetValue(titleId, out var attr);

        return new TitleFactSheet(
            title.Id,
            title.Name,
            title.Year,
            title.MediaType,
            title.Genres,
            title.Keywords.Take(JudgeKeywords).ToList(),
            Short(title.Overview, 400),
            title.RuntimeMinutes,
            title.Certification,
            credits.Where(c => c.Role == CreditRole.Director).Select(c => c.Name).Distinct().ToList(),
            credits.Where(c => c.Role == CreditRole.Actor)
                .OrderBy(c => c.CastOrder ?? int.MaxValue)
                .Select(c => c.Name)
                .Distinct()
                .Take(FactCast)
                .ToList(),
            hasAttributes ? attr.Themes : [],
            hasAttributes ? attr.Tone : null,
            seen);
    }

    /// <summary>The judge's view of <paramref name="titleIds"/>, in the order given; ids not in the catalogue are skipped.</summary>
    public async Task<IReadOnlyList<JudgeTitle>> JudgeTitlesAsync(IReadOnlyList<Guid> titleIds, CancellationToken ct)
    {
        if (titleIds.Count == 0)
        {
            return [];
        }

        var titles = await db.Titles.AsNoTracking().Where(t => titleIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var attributes = await TrustedAttributesAsync(titleIds, ct);
        return titleIds
            .Where(titles.ContainsKey)
            .Select(id =>
            {
                var t = titles[id];
                var found = attributes.TryGetValue(id, out var attr);
                return new JudgeTitle(
                    t.Id, t.Name, t.Year, t.MediaType, JudgeGenres(t), t.Keywords.Take(JudgeKeywords).ToList(),
                    Short(t.Overview, JudgeOverviewChars), found ? attr.Themes : [], found ? attr.Tone : null);
            })
            .ToList();
    }

    /// <summary>The marker on a genre the judge is given that the title's taxonomy could not tag (<see cref="JudgeGenres"/>).</summary>
    public const string InferredMarker = " (inferred)";

    /// <summary>
    /// The genres the judge reads for <paramref name="title"/>: its own, then every genre a TMDB
    /// taxonomy cannot tag on its medium that the catalogue nonetheless reads it as — by the stand-in
    /// keywords, alias genres and composites an exclusion of that genre applies
    /// (<see cref="Retrieval.GenreStandIns.ExcludedBy"/>) — marked <see cref="InferredMarker"/>.
    ///
    /// <para><b>Why (HREV3-ASK-D).</b> TMDB's TV list has no thriller, horror, romance, history or music
    /// genre. Eligibility and the supply already read a TV thriller by its keywords, but the judge saw
    /// only the raw genres, so for "spy thrillers" it ruled Alias, Nikita, Covert Affairs and Killing Eve
    /// "partial" because "its listed genres omit thriller": no series could ever be a pick for a
    /// thriller request. The judge now reads the genre the way the rest of the system does.</para>
    ///
    /// <para><b>Espionage reads as thriller on TV (HREV4, refuter round 4).</b> The stand-in vocabulary
    /// has no espionage words, so a spy series tagged only "spy", "espionage", "double agent" or "mi5"
    /// was still ruled partial: in one stored "spy thrillers" conversation, Alias, Nikita, Restless and
    /// Burn Notice ("no thriller evidence is given") and Spooks ("the listed genres omit thriller"). A
    /// spy series is a thriller series, so on TV — and only on TV, whose taxonomy cannot tag one — an
    /// espionage keyword infers thriller FOR THE JUDGE (<see cref="CarriesEspionage"/>), unless the show
    /// is a comedy, an animation or a family or kids' show: Archer, Chuck and Get Smart are spy comedies,
    /// and the judge may still call them a partial match. Eligibility is not widened here: the shared
    /// stand-ins are WS-3's, and an exclusion of thriller is theirs to extend.</para>
    /// </summary>
    internal static IReadOnlyList<string> JudgeGenres(Title title)
    {
        var inferred = Retrieval.GenreStandIns.Slugs
            .Where(slug => !title.Genres.Contains(slug, StringComparer.Ordinal) && Retrieval.GenreStandIns.ExcludedBy(title, slug))
            .ToList();
        if (title.MediaType == MediaType.Show
            && !title.Genres.Contains(ThrillerSlug, StringComparer.Ordinal)
            && !inferred.Contains(ThrillerSlug, StringComparer.Ordinal)
            && !title.Genres.Any(g => NotThrillerGenres.Contains(g))
            && CarriesEspionage(title.Keywords))
        {
            inferred.Add(ThrillerSlug);
            inferred.Sort(StringComparer.Ordinal);
        }

        return inferred.Count == 0 ? title.Genres : [.. title.Genres, .. inferred.Select(slug => slug + InferredMarker)];
    }

    /// <summary>
    /// Whether a keyword names espionage: a word "spy", "spies", "espionage", "cia", "mi5", "mi6" or
    /// "kgb" in it ("female spy", "central intelligence agency (cia)"), or the phrase "double agent",
    /// "secret agent", "intelligence agency", "secret service" or "british intelligence".
    /// </summary>
    internal static bool CarriesEspionage(IEnumerable<string> keywords) =>
        keywords.Any(keyword =>
        {
            var lower = keyword.ToLowerInvariant();
            return EspionagePhrases.Any(phrase => lower.Contains(phrase, StringComparison.Ordinal))
                || lower.Split(KeywordSeparators, StringSplitOptions.RemoveEmptyEntries).Any(EspionageWords.Contains);
        });

    private const string ThrillerSlug = "thriller";

    private static readonly HashSet<string> EspionageWords = new(StringComparer.Ordinal) { "spy", "spies", "espionage", "cia", "mi5", "mi6", "kgb" };

    private static readonly string[] EspionagePhrases = ["double agent", "secret agent", "intelligence agency", "secret service", "british intelligence"];

    private static readonly HashSet<string> NotThrillerGenres = new(StringComparer.Ordinal) { "comedy", "animation", "family", "kids", "children" };

    private static readonly char[] KeywordSeparators = [' ', '(', ')', ',', '-', '/', '.', ':', ';', '\'', '"'];

    /// <summary>
    /// Attributes only where extraction finished and its output is trusted; a pending or failed
    /// extraction contributes nothing rather than a guess.
    /// </summary>
    private async Task<Dictionary<Guid, (IReadOnlyList<string> Themes, string? Tone)>> TrustedAttributesAsync(
        IReadOnlyList<Guid> titleIds, CancellationToken ct) =>
        (await db.TitleAttributes
            .AsNoTracking()
            .Where(a => titleIds.Contains(a.TitleId) && a.Status == AttributeExtractionStatus.Done)
            .Select(a => new { a.TitleId, a.Themes, a.Tone })
            .ToListAsync(ct))
        .ToDictionary(a => a.TitleId, a => ((IReadOnlyList<string>)a.Themes.Take(6).ToList(), a.Tone));

    private static string? Short(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim();
        if (trimmed.Length <= max)
        {
            return trimmed;
        }

        var cut = trimmed.LastIndexOf(' ', max - 1);
        return string.Concat(trimmed.AsSpan(0, cut > max / 2 ? cut : max - 1), "…");
    }
}
