using System.Globalization;
using FrostAura.Reel.Application.Jobs;
using FrostAura.Reel.Application.Ranking;
using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Feed;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Infrastructure.Adapters;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// Runs the formatting helpers whose output must be byte-stable under deliberately hostile host
/// locales and asserts they ignore the ambient culture entirely.
///
/// <para>
/// Why this is a real risk and not a checkbox: nothing this service formats is localised. The
/// strings under test end up in a TMDB URL, an OpenRouter prompt, a cache key, a stored filter
/// row, a push notification and a why-sentence — each of which is either parsed by a machine or
/// compared as an exact string somewhere downstream. Under a comma-decimal locale a rating floor
/// leaves as <c>vote_average.gte=6,0</c>, which TMDB does not reject — it ignores the parameter
/// and returns a perfectly valid, perfectly unfiltered page. The failure mode of every site here
/// is a silently wrong result, never an exception.
/// </para>
///
/// <para>
/// The cultures are chosen to attack different things. <c>de-DE</c> and <c>fr-FR</c> swap the
/// decimal and group separators (and fr-FR's group separator is a narrow no-break space, which
/// survives a naive string comparison and breaks a URL). <c>ar-SA</c> is the calendar attack: its
/// default calendar is Umm al-Qura, so a <c>yyyy-MM-dd</c> date formatted under it carries a
/// Hijri year — the same shape, a different number, and no error anywhere.
/// </para>
///
/// <para>
/// The TMDB URL half of this contract lives next to the rest of that projection, in
/// <c>TmdbDiscoverQueryTests.The_discover_dates_stay_gregorian_under_a_hostile_host_calendar</c>,
/// so the golden query-string tests stay in one file.
/// </para>
/// </summary>
public class InvariantFormattingTests
{
    /// <summary>The hostile set. Every theory below runs against all three.</summary>
    public static TheoryData<string> HostileCultures() => new() { "de-DE", "fr-FR", "ar-SA" };

    private static T Under<T>(string culture, Func<T> body)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            return body();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void The_hostile_cultures_really_do_format_differently_unaided()
    {
        // A guard on the guard: if the runtime ever stopped honouring these cultures (an ICU-less
        // build, InvariantGlobalization flipped to true), every assertion below would pass for the
        // wrong reason and this file would quietly stop testing anything.
        Assert.Equal("4,5", Under("de-DE", () => 4.5m.ToString("0.0")));
        Assert.Equal("4,5", Under("fr-FR", () => 4.5m.ToString("0.0")));
        Assert.NotEqual(
            "2020-01-01",
            Under("ar-SA", () => new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).ToString("yyyy-MM-dd")));
    }

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void Stored_filter_rows_keep_their_canonical_shape(string culture)
    {
        // These strings are CAST to numeric inside Postgres by EligibilityQueryBuilder, so a
        // comma here is not a cosmetic difference — it is a runtime cast error on every query.
        Assert.Equal("7.5", Under(culture, () => ContentFilterRules.FormatRating(7.5m)));
        Assert.Equal("1999", Under(culture, () => ContentFilterRules.FormatYear(1999)));
        Assert.Equal(7.5m, Under(culture, () => ContentFilterRules.ParseRating("7.5")));
        Assert.Equal(1999, Under(culture, () => ContentFilterRules.ParseYear("1999")));

        // And the reader must NOT accept the hostile shape. Before 2026-09-10 it did, quietly and
        // catastrophically: NumberStyles.Number allows group separators, so a "7,5" row written by
        // a mis-cultured host read back as SEVENTY-FIVE, clamped to a crowd floor of 10, and
        // excluded every title in the catalogue with no error and no log line.
        Assert.Null(Under(culture, () => ContentFilterRules.ParseRating("7,5")));
        Assert.Null(Under(culture, () => ContentFilterRules.ParseYear("1,999")));
    }

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void The_filter_cost_cache_key_is_culture_stable(string culture)
    {
        var rules = FilterRuleSet.Create(
            excludeGenres: ["horror"],
            includeGenres: [],
            excludeKeywords: [],
            languages: ["en"],
            minCrowdRating: 7.5m,
            minYear: 1990,
            maxYear: 2010);

        var invariant = rules.CacheKey();
        var hostile = Under(culture, rules.CacheKey);

        // A cache key that moves with the host locale is not a cache — it is two caches that
        // disagree, and the one you read from depends on which thread served the request.
        Assert.Equal(invariant, hostile);
        Assert.Contains("7.5", hostile, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void Why_sentences_quote_ratings_with_a_dot(string culture)
    {
        var context = new ExplanationTemplater.Context(
            new Title
            {
                Id = Guid.NewGuid(),
                MediaType = MediaType.Movie,
                TraktSlug = "x",
                Name = "X",
                Genres = ["drama"],
            },
            [("directorAffinity", 1f)],
            new Dictionary<CreditRole, ExplanationTemplater.PersonAffinity>
            {
                [CreditRole.Director] = new("Denis Villeneuve", 8.4m),
            },
            new HashSet<string>(),
            AnchorName: null);

        var sentence = Under(culture, () => ExplanationTemplater.BuildSentence(context));

        Assert.Contains("8.4", sentence, StringComparison.Ordinal);
        Assert.DoesNotContain("8,4", sentence, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void The_rerank_prompt_quotes_predictions_with_a_dot(string culture)
    {
        var input = new RerankInput(
            Guid.NewGuid(), "Arrival", 2016, "Movie", ["science-fiction"], "Linguist meets heptapods.", 8.4m);

        var prompt = Under(
            culture,
            () => OpenRouterSearchAgent.BuildRerankPrompt([input], "thoughtful sci-fi", "you like slow burns"));

        // The model is told the prediction as a number it will echo back into a why-line. A
        // comma there reads to the model as a list separator, not a decimal point.
        Assert.Contains("predicted 8.4", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("8,4", prompt, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(HostileCultures))]
    public void The_digest_push_copy_quotes_the_rating_with_a_dot(string culture)
    {
        var item = new DigestItem(Guid.NewGuid(), 8.4m, DigestReasons.NewRelease);

        var payload = Under(
            culture,
            () => BuildDigestJobHandler.BuildPushPayload("Arrival", "movie", 329865L, item, itemCount: 3));

        Assert.Contains("8.4", payload.Title, StringComparison.Ordinal);
        Assert.DoesNotContain("8,4", payload.Title, StringComparison.Ordinal);
    }
}
