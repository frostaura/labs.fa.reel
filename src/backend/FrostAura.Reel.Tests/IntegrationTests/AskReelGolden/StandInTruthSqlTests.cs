using System.Text;
using FrostAura.Reel.Application.Search.Retrieval;

namespace FrostAura.Reel.Tests.IntegrationTests.AskReelGolden;

/// <summary>
/// The eval's SQL truth (<c>eval/askreel/truth/_eligible.sql</c>, <c>eval/askreel/supply/rules.sql</c>, and a
/// concept truth that reads a genre the way its exclusion does, <c>truth/romantic-comedy.sql</c>)
/// states the ExcludeGenre rule the way the API applies it, taxonomy gaps included: an alias genre, or a
/// stand-in keyword on a medium whose TMDB taxonomy cannot tag the genre (<see cref="GenreStandIns"/>).
/// Which labels stand in, and on which media, is the rule's DATA — 200-odd labels chosen by measurement —
/// so the SQL carries it as generated VALUES lists rather than a hand-typed copy that would drift the first
/// time the lists are re-derived (IREV3-SYS-02: before this, the truth knew no stand-ins at all, so the
/// scorer failed correct none-found turns and the leak check could not see a stand-in exclusion broken).
/// The MATCHING (exact label, whole-word stem, medium scope) stays hand-written in the SQL, and the
/// Postgres parity tests hold it to the builder on stand-in fixtures.
///
/// <para>Regenerate after changing <see cref="GenreStandIns"/>: run this class with
/// <c>REEL_REGENERATE_EVAL_SQL=1</c>, then review the diff.</para>
/// </summary>
public class StandInTruthSqlTests
{
    private static readonly string[] Files =
    [
        Path.Combine("eval", "askreel", "truth", "_eligible.sql"),
        Path.Combine("eval", "askreel", "supply", "rules.sql"),
        // HREV4-RET-01: "romantic comedies" is romance read as its exclusion reads it, AND comedy.
        Path.Combine("eval", "askreel", "truth", "romantic-comedy.sql"),
    ];

    [Fact]
    public void Every_truth_file_carries_the_stand_ins_and_aliases_GenreStandIns_defines()
    {
        var regenerate = Environment.GetEnvironmentVariable("REEL_REGENERATE_EVAL_SQL") == "1";
        foreach (var relative in Files)
        {
            var path = Path.Combine(RepoRoot(), relative);
            var current = File.ReadAllText(path);
            var expected = Render(current);
            if (regenerate && expected != current)
            {
                File.WriteAllText(path, expected);
                continue;
            }

            Assert.True(
                expected == current,
                $"{relative} is out of step with GenreStandIns — rerun {nameof(StandInTruthSqlTests)} with REEL_REGENERATE_EVAL_SQL=1 and review the diff.");
        }
    }

    [Fact]
    public void The_generated_lists_cover_every_slug_alias_and_label_with_its_media()
    {
        var standIns = StandInRows();
        Assert.Equal(GenreStandIns.Slugs.Count(s => GenreStandIns.MediaFor(s).Count > 0), standIns.Count);
        Assert.Equal(GenreStandIns.Patterns.Values.Sum(p => p.Count), standIns.Sum(r => r.Exact.Count + r.Words.Count));

        // The medium scoping is derived, never typed: romance has a movie id, so only TV is read by its
        // keywords; anime has an id on neither, so both are.
        Assert.Equal(new[] { "Show" }, standIns.Single(r => r.Slug == "romance").Media);
        Assert.Equal(new[] { "Movie", "Show" }, standIns.Single(r => r.Slug == "anime").Media);
        Assert.Contains("love of one''s life", Render(Template()), StringComparison.Ordinal);
        Assert.Contains("('thriller', ARRAY['suspense']::text[])", Render(Template()), StringComparison.Ordinal);

        // HREV1-RET-09: soap is romance on TV, and romance's overview phrases apply where its keywords do.
        Assert.Contains("('romance', ARRAY['soap']::text[])", Render(Template()), StringComparison.Ordinal);
        Assert.Contains("('romance', ARRAY['Show']::text[], ARRAY['romance', 'romantic', 'love story', 'in love', 'crush']::text[])", Render(Template()), StringComparison.Ordinal);
    }

    [Fact]
    public void The_generated_composite_row_states_the_rule_the_builder_applies()
    {
        // WS-3 CR-3 (H-7): "no anime" removes animation whose original language is Japanese. The generated
        // row must name exactly the facts MatchesComposite reads, so the SQL and the API cannot disagree on
        // which genre and language make a title anime.
        Assert.Contains("VALUES ('anime', 'animation', 'ja')", Render(Template()), StringComparison.Ordinal);
        Assert.Equal([GenreStandIns.AnimeSlug], GenreStandIns.CompositeSlugs);
        var anime = new FrostAura.Reel.Domain.Catalog.Title { Genres = [GenreStandIns.AnimeGenre], Language = "JA" };
        var dubbed = new FrostAura.Reel.Domain.Catalog.Title { Genres = [GenreStandIns.AnimeGenre], Language = "en" };
        Assert.True(GenreStandIns.MatchesComposite(anime, GenreStandIns.AnimeSlug));
        Assert.False(GenreStandIns.MatchesComposite(dubbed, GenreStandIns.AnimeSlug));
    }

    [Fact]
    public void A_missing_marker_pair_is_refused_rather_than_silently_generating_nothing()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Render("SELECT 1\n-- BEGIN GENERATED genre-aliases\n"));
        Assert.Contains("genre-aliases", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// <paramref name="sql"/> with the body between each pair of generated markers replaced by the list
    /// <see cref="GenreStandIns"/> defines, indented like its BEGIN marker.
    /// </summary>
    public static string Render(string sql)
    {
        var lines = sql.Split('\n').ToList();
        Replace(lines, "genre-aliases", AliasValues());
        Replace(lines, "genre-composites", CompositeValues());
        Replace(lines, "genre-stand-ins", StandInValues());
        Replace(lines, "genre-overview-phrases", OverviewValues());
        return string.Join('\n', lines);
    }

    private static void Replace(List<string> lines, string block, IReadOnlyList<string> body)
    {
        var begin = lines.FindIndex(l => l.Trim() == $"-- BEGIN GENERATED {block}");
        var end = lines.FindIndex(l => l.Trim() == $"-- END GENERATED {block}");
        if (begin < 0 || end < begin)
        {
            throw new InvalidOperationException($"the '-- BEGIN/END GENERATED {block}' markers are missing or out of order");
        }

        var indent = lines[begin][..(lines[begin].Length - lines[begin].TrimStart().Length)];
        lines.RemoveRange(begin + 1, end - begin - 1);
        lines.InsertRange(begin + 1, body.Select(l => indent + l));
    }

    private static List<string> AliasValues() =>
        Values(GenreStandIns.Aliases
            .OrderBy(a => a.Key, StringComparer.Ordinal)
            .Select(a => $"({Literal(a.Key)}, {Array(a.Value)})"));

    /// <summary>
    /// The composite slugs (WS-3 H-7: anime is animation whose original language is Japanese) as
    /// (slug, genre, language) rows. A composite <see cref="GenreStandIns"/> gains without a statement
    /// here is refused, so a new composite can never reach the API while the truth silently ignores it.
    /// </summary>
    private static List<string> CompositeValues() =>
        Values(GenreStandIns.CompositeSlugs
            .Order(StringComparer.Ordinal)
            .Select(slug => string.Equals(slug, GenreStandIns.AnimeSlug, StringComparison.Ordinal)
                ? $"({Literal(slug)}, {Literal(GenreStandIns.AnimeGenre)}, {Literal(GenreStandIns.AnimeLanguage)})"
                : throw new InvalidOperationException(
                    $"composite slug '{slug}' has no SQL statement — teach {nameof(StandInTruthSqlTests)}.{nameof(CompositeValues)} its facts")));

    private static List<string> StandInValues() =>
        Values(StandInRows().Select(r =>
            $"({Literal(r.Slug)}, {Array(r.Media)},\n   {Array(r.Exact)},\n   {Array(r.Words)})"));

    /// <summary>
    /// The overview stand-ins (<see cref="GenreStandIns.OverviewPhrases"/>, HREV1-RET-09) as (slug, media,
    /// phrases) rows, on the media the slug's keyword stand-ins apply to. The SQL joins the phrases into
    /// one alternation, so a phrase must be plain words; one carrying a regex metacharacter is refused
    /// here rather than matched as a pattern.
    /// </summary>
    private static List<string> OverviewValues() =>
        Values(GenreStandIns.OverviewPhrases
            .OrderBy(o => o.Key, StringComparer.Ordinal)
            .Select(o => o.Value.All(p => p.All(c => char.IsLetter(c) || c is ' ' or '\''))
                ? $"({Literal(o.Key)}, {Array(GenreStandIns.MediaFor(o.Key).Select(m => m.ToString()).ToList())}, {Array(o.Value)})"
                : throw new InvalidOperationException($"overview phrases for '{o.Key}' must be plain words")));

    private static List<string> Values(IEnumerable<string> rows)
    {
        var all = rows.ToList();
        var text = new StringBuilder("VALUES ");
        for (var i = 0; i < all.Count; i++)
        {
            text.Append(i == 0 ? string.Empty : ",\n       ").Append(all[i].Replace("\n", "\n       ", StringComparison.Ordinal));
        }

        return [.. text.ToString().Split('\n')];
    }

    private sealed record StandInRow(string Slug, IReadOnlyList<string> Media, IReadOnlyList<string> Exact, IReadOnlyList<string> Words);

    private static List<StandInRow> StandInRows() =>
        [.. GenreStandIns.Slugs
            .Where(slug => GenreStandIns.MediaFor(slug).Count > 0)
            .Select(slug => new StandInRow(
                slug,
                [.. GenreStandIns.MediaFor(slug).Select(m => m.ToString()).Order(StringComparer.Ordinal)],
                [.. GenreStandIns.Patterns[slug].OfType<GenreStandIns.Exact>().Select(p => p.Text)],
                [.. GenreStandIns.Patterns[slug].OfType<GenreStandIns.Word>().Select(p => p.Text)]))];

    /// <summary>A text[] literal, wrapped every eight labels so a diff names the label that moved.</summary>
    private static string Array(IReadOnlyList<string> values)
    {
        if (values.Count == 0)
        {
            return "ARRAY[]::text[]";
        }

        var chunks = values.Select(Literal).Chunk(8).Select(c => string.Join(", ", c));
        return $"ARRAY[{string.Join(",\n     ", chunks)}]::text[]";
    }

    private static string Literal(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string Template() =>
        "-- BEGIN GENERATED genre-aliases\n-- END GENERATED genre-aliases\n-- BEGIN GENERATED genre-composites\n-- END GENERATED genre-composites\n"
        + "-- BEGIN GENERATED genre-stand-ins\n-- END GENERATED genre-stand-ins\n"
        + "-- BEGIN GENERATED genre-overview-phrases\n-- END GENERATED genre-overview-phrases";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "fa.reel.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("fa.reel.slnx not found above the test assembly.");
    }
}
