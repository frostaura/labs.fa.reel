using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports.Tmdb;

namespace FrostAura.Reel.Tests.Ingestion;

/// <summary>
/// Regression for the defect the founder hit on 2026-08-07: a Spanish-language telenovela in an
/// English-only feed. Reel's mapping was correct — TMDB's data was not. <c>GET /3/tv/95439</c>
/// ("El final del paraíso", Telemundo) returns <c>original_language: "en"</c> alongside
/// <c>languages: ["es"]</c> and <c>spoken_languages: ["es"]</c>, and the hydrator trusted the one
/// field that was wrong.
///
/// Every expectation below was checked against live TMDB on 2026-08-07 before the rule was written,
/// including the two cases that kill the obvious-but-wrong version of it (<see
/// cref="First_spoken_language_is_not_the_rule_a_matching_original_wins_even_when_it_is_last"/> and
/// <see cref="A_no_language_sentinel_never_overrides_the_original_language"/>).
/// </summary>
public class TmdbLanguageCorroborationTests
{
    [Fact]
    public void A_spoken_list_that_contradicts_original_language_wins()
    {
        // tv/95439 — the reported bug, verbatim from the live payload.
        Assert.Equal("es", TmdbLanguage.Resolve("en", ["es"], ["es"]));
    }

    [Fact]
    public void A_corroborated_original_language_is_kept()
    {
        // movie/76341 Mad Max: Fury Road — plain English film, nothing to correct.
        Assert.Equal("en", TmdbLanguage.Resolve("en", ["en"], null));
    }

    [Fact]
    public void An_english_film_with_some_foreign_dialogue_stays_english()
    {
        // movie/19995 Avatar — spoken ["en","es"]. "en" IS in the list, so the list corroborates
        // rather than contradicts. This is the case that forbids a naive "first spoken wins".
        Assert.Equal("en", TmdbLanguage.Resolve("en", ["en", "es"], null));
    }

    [Fact]
    public void First_spoken_language_is_not_the_rule_a_matching_original_wins_even_when_it_is_last()
    {
        // movie/496243 Parasite — original "ko", spoken ["en","de","ko"]. TMDB orders that array
        // arbitrarily, so "take the first spoken language" would relabel a Korean film as English.
        // Membership is the test; order is consulted only after membership has failed.
        Assert.Equal("ko", TmdbLanguage.Resolve("ko", ["en", "de", "ko"], null));
    }

    [Fact]
    public void A_no_language_sentinel_never_overrides_the_original_language()
    {
        // Silent films carry TMDB's "No Language" row: movie/19 Metropolis (original "de"),
        // movie/74643 The Artist ("fr"), movie/961 The General ("en") all return spoken ["xx"].
        // Unfiltered, the rule would relabel all three as "xx" and drop the English ones out of the
        // English-only allow-list — a regression, not a fix.
        Assert.Equal("de", TmdbLanguage.Resolve("de", ["xx"], null));
        Assert.Equal("en", TmdbLanguage.Resolve("en", ["xx"], null));
        // A sentinel mixed in with real codes is simply ignored, and the real codes still decide.
        Assert.Equal("es", TmdbLanguage.Resolve("en", ["xx", "es"], null));
        Assert.Equal("en", TmdbLanguage.Resolve("en", ["xx", "en"], null));
    }

    [Fact]
    public void An_empty_or_null_spoken_list_leaves_the_original_language_alone()
    {
        Assert.Equal("en", TmdbLanguage.Resolve("en", [], null));
        Assert.Equal("en", TmdbLanguage.Resolve("en", null, null));
        Assert.Equal("en", TmdbLanguage.Resolve("en", null, []));
    }

    [Fact]
    public void A_title_with_no_language_data_at_all_resolves_to_null_so_callers_keep_what_they_had()
    {
        Assert.Null(TmdbLanguage.Resolve(null, null, null));
        Assert.Null(TmdbLanguage.Resolve("", [], []));
        Assert.Null(TmdbLanguage.Resolve("   ", ["xx"], null));
    }

    [Fact]
    public void A_missing_original_language_is_filled_from_the_spoken_list()
    {
        // Nothing to defend, so the corroborators are the only evidence available.
        Assert.Equal("ja", TmdbLanguage.Resolve(null, ["ja"], null));
        Assert.Equal("fr", TmdbLanguage.Resolve(null, null, ["fr"]));
    }

    [Fact]
    public void Tv_languages_is_the_fallback_only_when_spoken_languages_is_absent()
    {
        // Movies never send `languages`; TV sends both and, across the 3,729-title census run on
        // 2026-08-07, the two never disagreed. spoken_languages therefore leads and `languages`
        // only speaks when spoken_languages is empty or sentinel-only.
        Assert.Equal("es", TmdbLanguage.Resolve("en", null, ["es"]));
        Assert.Equal("es", TmdbLanguage.Resolve("en", [], ["es"]));
        Assert.Equal("es", TmdbLanguage.Resolve("en", ["xx"], ["es"]));
        // spoken_languages present and corroborating ⇒ `languages` is not consulted at all.
        Assert.Equal("en", TmdbLanguage.Resolve("en", ["en"], ["es"]));
    }

    [Fact]
    public void Codes_are_normalised_so_case_and_whitespace_cannot_fake_a_contradiction()
    {
        Assert.Equal("en", TmdbLanguage.Resolve("EN", [" en "], null));
        Assert.Equal("es", TmdbLanguage.Resolve("EN", ["ES"], null));
    }

    [Fact]
    public async Task The_hydrator_stores_the_corroborated_language_not_original_language()
    {
        // End to end through the code path the bug actually took: the telenovela's payload.
        var title = NewTitle();
        await Hydrate(title, Details(title.TmdbId!.Value, original: "en", spoken: ["es"], languages: ["es"]));
        Assert.Equal("es", title.Language);

        // And the ordinary case is untouched.
        var english = NewTitle();
        await Hydrate(english, Details(english.TmdbId!.Value, original: "en", spoken: ["en"], languages: null));
        Assert.Equal("en", english.Language);

        // TMDB silent on language ⇒ whatever Trakt's IngestMapper already set survives.
        var fromTrakt = NewTitle();
        fromTrakt.Language = "de";
        await Hydrate(fromTrakt, Details(fromTrakt.TmdbId!.Value, original: null, spoken: null, languages: null));
        Assert.Equal("de", fromTrakt.Language);
    }

    private static Title NewTitle() => new()
    {
        Id = Guid.NewGuid(),
        MediaType = MediaType.Show,
        TmdbId = Random.Shared.NextInt64(1, 1_000_000),
        TraktSlug = "fixture",
        Name = "fixture",
        CreatedAt = DateTime.UtcNow,
    };

    private static TmdbTitleDetails Details(long id, string? original, string[]? spoken, string[]? languages) =>
        new(id, "/p.jpg", null, 1m, 5m, 10, 45, null, null, null, [], [], null, [], original, spoken, languages);

    private static async Task Hydrate(Title title, TmdbTitleDetails details)
    {
        // Reuses the resilience suite's credit-free stub + inert db context: these fixtures carry no
        // credits, so a correct hydrator never reaches persistence.
        var tmdb = new TitleHydratorResilienceTests.StubTmdb(
            failFor: null, new InvalidOperationException("unused"), details: _ => details);
        var result = await TitleHydratorResilienceTests.Hydrator(tmdb)
            .HydrateBatchResilientAsync([title], CancellationToken.None);
        Assert.Equal(1, result.Hydrated);
    }
}
