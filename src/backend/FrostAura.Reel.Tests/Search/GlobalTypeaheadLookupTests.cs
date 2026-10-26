using FrostAura.Reel.Application.Search.Typeahead;
using FrostAura.Reel.Domain.Ports.Tmdb;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// The pure half of the typeahead's global TMDB leg: movie and show pages unioned, deduplicated by
/// (medium, id), poster-less rows dropped, most popular first. The half that judges every row by
/// the account's conditions (FD-5) needs a database and lives in <c>TypeaheadSearchTests</c>.
/// </summary>
public class GlobalTypeaheadLookupTests
{
    [Fact]
    public void Unions_dedupes_and_orders_by_popularity_across_media()
    {
        IReadOnlyList<TmdbListItem> movies =
        [
            Item(1, true, "Jeanne Dielman", popularity: 12m),
            Item(3, true, "News from Home", popularity: 6m),
            Item(1, true, "Jeanne Dielman (duplicate page row)", popularity: 12m),
        ];
        IReadOnlyList<TmdbListItem> shows = [Item(4, false, "Dielman: The Series", popularity: 30m), Item(1, false, "Same id, other medium", popularity: 1m)];

        var rows = TypeaheadSearch.Union([movies, shows]);

        Assert.Equal([(false, 4L), (true, 1L), (true, 3L), (false, 1L)], rows.Select(r => (r.IsMovie, r.Id)));
    }

    [Fact]
    public void Drops_posterless_rows_even_at_the_top_of_popularity()
    {
        IReadOnlyList<TmdbListItem> movies =
        [
            Item(99, true, "No Poster", popularity: 999m, posterPath: null),
            Item(1, true, "Movie 1", popularity: 10m),
        ];

        Assert.Equal([1L], TypeaheadSearch.Union([movies]).Select(r => r.Id));
    }

    private static TmdbListItem Item(long id, bool isMovie, string name, decimal popularity, string? posterPath = "/p.jpg") =>
        new(id, isMovie, name, posterPath, null, "An overview.", popularity, 7m, 100,
            new DateTime(1975, 5, 14, 0, 0, 0, DateTimeKind.Utc), [18]);
}
