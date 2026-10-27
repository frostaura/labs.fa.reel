using System.Linq.Expressions;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>One genre chip the conditions editor can offer, with what excluding it would cover.</summary>
/// <param name="Slug">The value an ExcludeGenre / IncludeGenre row stores ("comedy", "reality", "anime").</param>
/// <param name="Movies">Films in the catalogue an exclusion of this slug removes (genre, alias, composite or stand-in).</param>
/// <param name="Shows">Shows in the catalogue an exclusion of this slug removes.</param>
/// <param name="Composite">True for a slug that is a combination of facts rather than a tag: anime is animation in Japanese.</param>
/// <param name="Offered">
/// True when the chip covers at least <see cref="FilterChipReport.MinTitles"/> titles. A chip under the
/// bar is listed (so a saved rule for it still has a row to render) but not offered for new taps: a
/// chip that removes 2 titles reads as a control that does nothing (H-7: "sports" matched 0).
/// </param>
public sealed record FilterChip(string Slug, int Movies, int Shows, bool Composite, bool Offered)
{
    /// <summary>Films plus shows.</summary>
    public int Total => Movies + Shows;
}

/// <summary>The catalogue's genre chips, biggest first.</summary>
/// <param name="MinTitles">The bar a chip must reach to be offered.</param>
public sealed record FilterChipReport(int MinTitles, IReadOnlyList<FilterChip> Chips);

/// <summary>
/// Catalogue counts behind the conditions editor's genre chips (H-7), derived from the catalogue
/// instead of a hand-written list: on live the hand-written chips included "sports" (0 titles) and
/// "anime" (2), while reality (183 shows), soap (229), talk-show (46) and news (10) could not be
/// excluded at all.
///
/// <para><b>What a count means.</b> The titles an ExcludeGenre of that slug would REMOVE — exactly
/// <see cref="FilterBreaches.ExcludedGenre"/>, the predicate the eligibility builder applies — so the
/// number on a chip is the number that chip actually controls: the genre tag, its alias genres (Trakt's
/// <c>children</c> counts under family, <c>suspense</c> under thriller, <c>soap</c> under romance), the
/// anime composite (animation in Japanese) and the TV stand-ins (keywords, and romance's overview
/// phrases). A name-only alias is folded into its target and never listed on its own; soap, a genre in
/// its own right, keeps its own chip too. Counts are catalogue-wide, not per account: a chip's reach does not
/// depend on what the viewer has seen.</para>
///
/// <para><b>One pass.</b> Every title is projected to the bitmask of chips it falls under and grouped
/// by (medium, mask); the per-chip counts are summed from the groups. Cached for
/// <see cref="CacheWindow"/>, because the catalogue moves slowly and the editor asks on every open.</para>
/// </summary>
public sealed class FilterChipCounts(IReelDbContext db, IMemoryCache cache)
{
    /// <summary>A chip must cover at least this many titles to be offered (the editor's bar, H-7).</summary>
    public const int MinTitles = 50;

    /// <summary>How long the counts stay warm.</summary>
    public static readonly TimeSpan CacheWindow = TimeSpan.FromMinutes(30);

    private const string CacheKey = "filterchips:v1";
    private const int BitsPerMask = 63;

    /// <summary>The catalogue's chips with their counts, offered chips first, each group biggest first.</summary>
    public async Task<FilterChipReport> CountAsync(CancellationToken ct)
    {
        if (cache.TryGetValue<FilterChipReport>(CacheKey, out var cached) && cached is not null)
        {
            return cached;
        }

        // A name-only alias (children, suspense) is folded into its target; one that is a genre in its
        // own right (soap) keeps its chip — "no soaps" is a condition of its own.
        var aliasSlugs = GenreStandIns.Aliases.Values
            .SelectMany(a => a)
            .Where(a => !GenreStandIns.AliasesWithTheirOwnChip.Contains(a))
            .ToHashSet(StringComparer.Ordinal);
        var tagged = await db.Titles
            .SelectMany(t => t.Genres)
            .Distinct()
            .ToListAsync(ct);
        var slugs = tagged
            .Concat(GenreStandIns.Slugs)
            .Concat(GenreStandIns.CompositeSlugs)
            .Select(s => s.Trim().ToLowerInvariant())
            .Where(s => s.Length > 0 && !aliasSlugs.Contains(s))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Take(2 * BitsPerMask)
            .ToList();

        var t = Expression.Parameter(typeof(Title), "t");
        Expression m0 = Expression.Constant(0L);
        Expression m1 = Expression.Constant(0L);
        for (var i = 0; i < slugs.Count; i++)
        {
            var covers = FilterBreaches.BodyOn(FilterBreaches.ExcludedGenre(slugs[i]), t);
            var term = Expression.Condition(covers, Expression.Constant(1L << (i % BitsPerMask)), Expression.Constant(0L));
            if (i < BitsPerMask)
            {
                m0 = Expression.Or(m0, term);
            }
            else
            {
                m1 = Expression.Or(m1, term);
            }
        }

        var projection = Expression.Lambda<Func<Title, ChipShape>>(
            Expression.MemberInit(
                Expression.New(typeof(ChipShape)),
                Expression.Bind(typeof(ChipShape).GetProperty(nameof(ChipShape.Media))!, Expression.Property(t, nameof(Title.MediaType))),
                Expression.Bind(typeof(ChipShape).GetProperty(nameof(ChipShape.M0))!, m0),
                Expression.Bind(typeof(ChipShape).GetProperty(nameof(ChipShape.M1))!, m1)),
            t);

        var groups = await db.Titles
            .Select(projection)
            .Where(s => s.M0 != 0 || s.M1 != 0)
            .GroupBy(s => new { s.Media, s.M0, s.M1 })
            .Select(g => new { g.Key.Media, g.Key.M0, g.Key.M1, Count = g.Count() })
            .ToListAsync(ct);

        var chips = new List<FilterChip>(slugs.Count);
        for (var i = 0; i < slugs.Count; i++)
        {
            var bit = 1L << (i % BitsPerMask);
            var inMask = groups.Where(g => ((i < BitsPerMask ? g.M0 : g.M1) & bit) != 0).ToList();
            var movies = inMask.Where(g => g.Media == MediaType.Movie).Sum(g => g.Count);
            var shows = inMask.Where(g => g.Media == MediaType.Show).Sum(g => g.Count);
            var composite = GenreStandIns.CompositeSlugs.Contains(slugs[i], StringComparer.Ordinal);
            chips.Add(new FilterChip(slugs[i], movies, shows, composite, movies + shows >= MinTitles));
        }

        var report = new FilterChipReport(
            MinTitles,
            [.. chips.OrderByDescending(c => c.Offered).ThenByDescending(c => c.Total).ThenBy(c => c.Slug, StringComparer.Ordinal)]);
        cache.Set(CacheKey, report, CacheWindow);
        return report;
    }

    /// <summary>One title's medium and the chips it falls under, as Postgres computes them.</summary>
    private sealed class ChipShape
    {
        public MediaType Media { get; init; }

        public long M0 { get; init; }

        public long M1 { get; init; }
    }
}
