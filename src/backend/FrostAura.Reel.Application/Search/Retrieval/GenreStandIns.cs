using System.Linq.Expressions;
using System.Reflection;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports.Tmdb;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>
/// How a genre a TMDB taxonomy cannot express is recognised anyway: the TMDB keyword labels that
/// stand in for it, on the media whose taxonomy has no id for it. This is what makes an
/// <c>ExcludeGenre</c> airtight for TV as well as for movies.
///
/// <para><b>Why it exists.</b> TMDB's TV genre list has no Romance, Horror, History, Thriller or Music
/// (it fuses pairs instead: "Action &amp; Adventure", "Sci-Fi &amp; Fantasy", "War &amp; Politics").
/// TMDB tags those shows with KEYWORDS instead — 835 shows on the founder's catalogue carry a
/// romance keyword, while only 3 carry the romance genre. So "no romance, horror or history" removed
/// almost nothing from TV, and the spec's "an exclusion is airtight on every surface" was false for
/// half the catalogue. Neither taxonomy has an id at all for <c>anime</c> or <c>musical</c>, which
/// is why two of the founder's twelve conditions matched almost nothing on either medium.</para>
///
/// <para><b>Which media a stand-in applies to</b> is derived, never typed: a medium whose TMDB
/// taxonomy HAS an id for the slug tags its titles with the genre itself, so the stand-in is not
/// applied there (a thriller film with a "romance" keyword is not a romance film — TMDB would have
/// said so). A medium with no id is judged by its keywords. That keeps movies exactly as strict as
/// they were and makes TV as strict as movies.</para>
///
/// <para><b>Matching.</b> A <see cref="Exact"/> pattern matches the whole label. A <see cref="Word"/>
/// pattern matches the stem as a whole word or phrase inside a label ("teenage romance",
/// "romance musical"), but never inside another word ("bromance" is not romance). Labels are stored
/// lower-cased by ingest, and the stems here are lower-case.</para>
///
/// <para><b>Exclusions only.</b> An <c>IncludeGenre</c> allow-list still reads the genre tags alone:
/// a stand-in widening an allow-list would admit titles the user never asked for, which is the
/// opposite direction from the lock this exists to hold.</para>
/// </summary>
public static class GenreStandIns
{
    /// <summary>One way a keyword label can stand in for a genre.</summary>
    public abstract record Pattern(string Text);

    /// <summary>The label equals <see cref="Pattern.Text"/>.</summary>
    public sealed record Exact(string Text) : Pattern(Text);

    /// <summary>The label contains <see cref="Pattern.Text"/> as a whole word or phrase.</summary>
    public sealed record Word(string Text) : Pattern(Text);

    /// <summary>
    /// Every genre slug that has keyword stand-ins, with its patterns. Chosen from the catalogue's
    /// own vocabulary (2026-10-24, 38,262 titles): each pattern was read against the labels it
    /// matches, and broad stems that catch unrelated labels are written as exact labels instead
    /// ("history" alone also matches "dating history" and "alternate history", so history is the
    /// exact label plus the <c>historical</c> stem and the period-piece labels).
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<Pattern>> Patterns =
        new Dictionary<string, IReadOnlyList<Pattern>>(StringComparer.Ordinal)
        {
            ["romance"] = [new Word("romance"), new Word("romantic")],
            ["horror"] = [new Word("horror"), new Word("slasher")],
            ["history"] =
            [
                new Exact("history"), new Word("historical"), new Exact("period drama"),
                new Exact("period piece"), new Exact("ancient history"), new Exact("world history"),
            ],
            ["thriller"] = [new Word("thriller")],
            ["music"] = [new Exact("music"), new Word("musician"), new Exact("concert")],
            ["musical"] =
            [
                new Exact("musical"), new Exact("jukebox musical"), new Exact("broadway musical"),
                new Exact("stage musical"), new Exact("rock musical"), new Exact("musical comedy"),
                new Exact("musical drama"), new Exact("musical theater"), new Exact("musical adaptation"),
                new Exact("musical biopic"), new Exact("dance musical"), new Exact("horror musical"),
                new Exact("romance musical"), new Exact("cabaret musical"),
            ],
            ["anime"] = [new Exact("anime")],
        };

    /// <summary>
    /// Genre slugs that count as another slug for an exclusion. TMDB's TV "Kids" folds into
    /// <c>family</c> at ingest (<see cref="TmdbGenres.TvIdToSlugs"/>), but a show synced from Trakt keeps
    /// Trakt's own <c>children</c> slug — so "no family" has to reach it by name.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Aliases =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["family"] = ["children"],
        };

    /// <summary>The alias slugs an exclusion of <paramref name="slug"/> also removes; empty when none.</summary>
    public static IReadOnlyList<string> AliasesOf(string slug) =>
        Aliases.TryGetValue(slug, out var aliases) ? aliases : [];

    /// <summary>
    /// True when an ExcludeGenre of <paramref name="slug"/> removes <paramref name="title"/>: the genre
    /// itself, an alias genre, or a stand-in keyword on a medium that cannot tag it (in memory).
    /// </summary>
    public static bool ExcludedBy(Title title, string slug) =>
        title.Genres.Contains(slug, StringComparer.Ordinal)
        || AliasesOf(slug).Any(alias => title.Genres.Contains(alias, StringComparer.Ordinal))
        || Carries(title, slug);

    /// <summary>The slugs with stand-ins, in a stable order so the generated SQL is stable too.</summary>
    public static IReadOnlyList<string> Slugs { get; } =
        [.. Patterns.Keys.OrderBy(slug => slug, StringComparer.Ordinal)];

    /// <summary>
    /// The media whose TMDB taxonomy has no id for <paramref name="slug"/> — the media a stand-in is
    /// applied to. Empty when both taxonomies can express the slug (no stand-in is needed).
    /// </summary>
    public static IReadOnlyList<MediaType> MediaFor(string slug)
    {
        if (!TmdbGenres.SlugToIds.TryGetValue(slug, out var ids))
        {
            return [MediaType.Movie, MediaType.Show];
        }

        var media = new List<MediaType>(2);
        if (ids.Movie is null)
        {
            media.Add(MediaType.Movie);
        }

        if (ids.Tv is null)
        {
            media.Add(MediaType.Show);
        }

        return media;
    }

    /// <summary>True when <paramref name="label"/> stands in for <paramref name="slug"/> (in memory).</summary>
    public static bool Matches(string slug, string label) =>
        Patterns.TryGetValue(slug, out var patterns)
        && patterns.Any(p => p switch
        {
            Exact exact => string.Equals(label, exact.Text, StringComparison.Ordinal),
            Word word => string.Equals(label, word.Text, StringComparison.Ordinal)
                || label.StartsWith(word.Text + " ", StringComparison.Ordinal)
                || label.EndsWith(" " + word.Text, StringComparison.Ordinal)
                || label.Contains(" " + word.Text + " ", StringComparison.Ordinal),
            _ => false,
        });

    /// <summary>
    /// True when <paramref name="title"/> carries a stand-in for <paramref name="slug"/> on a medium
    /// the stand-in applies to (in memory). The in-memory twin of <see cref="CarriesStandIn"/>.
    /// </summary>
    public static bool Carries(Title title, string slug) =>
        MediaFor(slug).Contains(title.MediaType) && title.Keywords.Any(k => Matches(slug, k));

    /// <summary>
    /// The SQL-translatable predicate "this title carries a stand-in keyword for
    /// <paramref name="slug"/> on a medium whose taxonomy cannot tag it". Always false for a slug
    /// with no stand-ins or no applicable medium.
    /// </summary>
    public static Expression<Func<Title, bool>> CarriesStandIn(string slug)
    {
        var media = MediaFor(slug);
        if (!Patterns.TryGetValue(slug, out var patterns) || patterns.Count == 0 || media.Count == 0)
        {
            return t => false;
        }

        var label = Expression.Parameter(typeof(string), "k");
        var labelMatch = patterns
            .Select(p => LabelMatch(label, p))
            .Aggregate(Expression.OrElse);
        var labelLambda = Expression.Lambda<Func<string, bool>>(labelMatch, label);

        var title = Expression.Parameter(typeof(Title), "t");
        var keywords = Expression.Property(title, nameof(Title.Keywords));
        var anyKeyword = Expression.Call(AnyMethod, keywords, labelLambda);

        // Both media ⇒ no media test at all; one ⇒ an equality on MediaType.
        Expression body = media.Count == 2
            ? anyKeyword
            : Expression.AndAlso(
                Expression.Equal(Expression.Property(title, nameof(Title.MediaType)), Expression.Constant(media[0])),
                anyKeyword);
        return Expression.Lambda<Func<Title, bool>>(body, title);
    }

    private static readonly MethodInfo AnyMethod = typeof(Enumerable)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(Enumerable.Any) && m.GetParameters().Length == 2)
        .MakeGenericMethod(typeof(string));

    private static readonly MethodInfo StartsWithMethod =
        typeof(string).GetMethod(nameof(string.StartsWith), [typeof(string)])!;

    private static readonly MethodInfo EndsWithMethod =
        typeof(string).GetMethod(nameof(string.EndsWith), [typeof(string)])!;

    private static readonly MethodInfo ContainsMethod =
        typeof(string).GetMethod(nameof(string.Contains), [typeof(string)])!;

    /// <summary>
    /// One pattern as an expression over the label <paramref name="k"/>. The single-argument string
    /// overloads are the ones EF translates (to <c>=</c> and <c>LIKE</c>); they are culture-free in
    /// SQL, and the in-memory twin above uses ordinal comparison to match.
    /// </summary>
    private static Expression LabelMatch(ParameterExpression k, Pattern pattern)
    {
        var equal = Expression.Equal(k, Expression.Constant(pattern.Text));
        if (pattern is Exact)
        {
            return equal;
        }

        return Expression.OrElse(
            equal,
            Expression.OrElse(
                Expression.Call(k, StartsWithMethod, Expression.Constant(pattern.Text + " ")),
                Expression.OrElse(
                    Expression.Call(k, EndsWithMethod, Expression.Constant(" " + pattern.Text)),
                    Expression.Call(k, ContainsMethod, Expression.Constant(" " + pattern.Text + " ")))));
    }
}
