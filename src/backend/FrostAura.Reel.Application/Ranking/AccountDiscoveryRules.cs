using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Filters;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;
using Microsoft.EntityFrameworkCore;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// The account's standing content rules, projected into the parameters TMDB discover actually
/// understands.
///
/// <para>
/// This is the supply-side half of <see cref="Search.EligibilityQueryBuilder"/>, and the two must be
/// read together: eligibility is the airtight rule and stays airtight — nothing here relaxes it —
/// while this type decides how much of it can be pushed INTO the fetch instead of being applied
/// after it. On the founder's account the difference is not cosmetic: ~421 of 5,319 catalogue titles
/// are eligible, so an unfiltered discover page was ~92% waste, every build, for ever.
/// </para>
///
/// <para>
/// Deliberately partial. <c>ExcludeKeyword</c> is not projected because TMDB's
/// <c>without_keywords</c> takes ids and resolving every keyword rule would cost a search call per
/// rule per build; <c>MaxMaturity</c> has no discover parameter at all; <c>MinPredictedRating</c>
/// needs a model. Those three stay where they already are — the eligibility query and the scoring
/// surfaces — so the projection is a strict subset of the rule, never a re-statement of it that
/// could drift.
/// </para>
/// </summary>
/// <param name="ExcludedGenreSlugs">Every ExcludeGenre value, lower-cased — including the slugs TMDB
/// has no id for, because those still have to be kept out of the top-genre selection.</param>
/// <param name="ExcludedMovieGenreIds">The subset TMDB's movie id space can express.</param>
/// <param name="ExcludedTvGenreIds">The subset TMDB's TV id space can express — smaller, because the
/// TV taxonomy has no horror, thriller or romance genre at all.</param>
/// <param name="OriginalLanguage">The single language to constrain on, or null for unconstrained.</param>
/// <param name="MinCrowdRating">The MinCrowdRating floor, or null when the account sets none.</param>
/// <param name="ReleasedAfter">MinYear as a date bound.</param>
/// <param name="ReleasedBefore">MaxYear as a date bound.</param>
public sealed record AccountDiscoveryRules(
    IReadOnlySet<string> ExcludedGenreSlugs,
    IReadOnlyList<int> ExcludedMovieGenreIds,
    IReadOnlyList<int> ExcludedTvGenreIds,
    string? OriginalLanguage,
    decimal? MinCrowdRating,
    DateTime? ReleasedAfter,
    DateTime? ReleasedBefore)
{
    /// <summary>
    /// The vote-count floor the feed's own discover has always used. Carried explicitly into the
    /// concept endpoint, whose default is 10, so routing supply through it does not quietly drop the
    /// build's quality floor to a third of what it was.
    /// </summary>
    public const int FeedVoteCountFloor = 50;

    /// <summary>The default when the account has configured no Language row (founder ask 2026-06-30).</summary>
    public const string DefaultLanguage = "en";

    /// <summary>The excluded genre ids for one medium.</summary>
    public IReadOnlyList<int> ExcludedGenreIds(bool movies) => movies ? ExcludedMovieGenreIds : ExcludedTvGenreIds;

    /// <summary>
    /// These rules as discover options. <paramref name="peopleIds"/> is the credit-affinity arm's
    /// constraint and is ignored by TMDB for TV — see <see cref="TmdbDiscoverOptions"/>.
    /// </summary>
    public TmdbDiscoverOptions Options(IReadOnlyList<long>? peopleIds = null) => new(
        OriginalLanguage,
        MinCrowdRating,
        FeedVoteCountFloor,
        peopleIds,
        // Background supply drains the Reconcile lane, exactly as the feed's old DiscoverAsync did.
        // Routing through the concept endpoint must not promote ~40 background calls per build into
        // the Interactive lane that live search and Ask Reel share.
        RatePriority.Reconcile);

    /// <summary>Reads every content rule for the account in one round trip and projects it.</summary>
    public static async Task<AccountDiscoveryRules> LoadAsync(IReelDbContext db, Guid accountId, CancellationToken ct)
    {
        var rows = await db.ContentFilters
            .Where(f => f.AccountId == accountId)
            .Select(f => new { f.Kind, f.Value })
            .ToListAsync(ct);

        string[] Values(FilterKind kind) => rows
            .Where(r => r.Kind == kind)
            .Select(r => r.Value.Trim().ToLowerInvariant())
            .Where(v => v.Length > 0)
            .Distinct()
            .ToArray();

        var excluded = Values(FilterKind.ExcludeGenre).ToHashSet();
        var excludedMovie = new List<int>();
        var excludedTv = new List<int>();
        foreach (var slug in excluded.OrderBy(s => s, StringComparer.Ordinal))
        {
            if (!TmdbGenres.SlugToIds.TryGetValue(slug, out var ids))
            {
                continue;
            }

            if (ids.Movie is { } movieId)
            {
                excludedMovie.Add(movieId);
            }

            if (ids.Tv is { } tvId)
            {
                excludedTv.Add(tvId);
            }
        }

        // Language is an ALLOW-list of one, as far as TMDB is concerned: with_original_language takes
        // a single code. "*" means the account opted every language back in, and two or more codes
        // cannot be expressed — in both cases the honest projection is "no constraint", leaving the
        // eligibility query to enforce the real rule. Sending one of several would silently discard
        // the user's other languages at the fetch, which no downstream layer could recover from.
        var languages = Values(FilterKind.Language);
        var language = languages.Length switch
        {
            0 => DefaultLanguage,
            1 when languages[0] != "*" => languages[0],
            _ => null,
        };

        decimal? minCrowd = decimal.TryParse(
            Values(FilterKind.MinCrowdRating).FirstOrDefault(),
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var crowd) && crowd > 0m
            ? Math.Clamp(crowd, 0m, 10m)
            : null;

        DateTime? Bound(FilterKind kind, bool upper)
        {
            // ContentFilterRules is the only sanctioned writer of these rows and reads them back
            // invariantly; going through it keeps this reader from inventing a second shape.
            if (ContentFilterRules.ParseYear(Values(kind).FirstOrDefault()) is not { } year
                || year is < 1870 or > 2999)
            {
                return null;
            }

            return upper
                ? new DateTime(year, 12, 31, 0, 0, 0, DateTimeKind.Utc)
                : new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        return new AccountDiscoveryRules(
            excluded,
            excludedMovie,
            excludedTv,
            language,
            minCrowd,
            Bound(FilterKind.MinYear, upper: false),
            Bound(FilterKind.MaxYear, upper: true));
    }
}
