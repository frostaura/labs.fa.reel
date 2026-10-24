using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
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
    /// Every genre slug that has keyword stand-ins, with its patterns.
    ///
    /// <para><b>How the TV-gap lists were chosen (2026-10-24, slot copy, 31,546 films / 6,716
    /// shows).</b> Films are the medium where the answer is known — TMDB tags them with the genre — so
    /// each candidate label was scored on films: of the films carrying it, the share TMDB tags with
    /// the genre. A label stands in when that share is at least 0.6 on at least 15 films AND the
    /// label names the genre's own subject (a love story; a monster, a haunting, gore; a historical
    /// era or nation's history; music-making), never an incidental setting, object or production
    /// trait — "woods", "shark", "basement", "scuba diving" and "low budget" all clear 0.6 on films
    /// and would wrongly empty TV of nature and diving shows. The genre's own name and adjective are
    /// whole-word stems ("romance", "romantic", "horror", "thriller", "historical", "music"). A
    /// "nation history" label qualifies on fewer films (at least 4) because the subject is the genre.
    /// Labels that fail the bar were dropped even when they read as on-genre — "period drama" (0.36
    /// of films are History), "19th century" (0.28), "victorian england" (0.19), "musician" (0.54) —
    /// because applying them to TV would make TV STRICTER than films, not as strict.</para>
    ///
    /// <para><b>What that buys, measured on films (the stand-ins scored against TMDB's own genre
    /// tags).</b> Share of genre-tagged films the stand-ins catch, before → after: romance 0.12 →
    /// 0.34, horror 0.22 → 0.49, history 0.12 → 0.14, thriller 0.07 → 0.16, music 0.16 → 0.44; with
    /// precision 0.68, 0.81, 0.57, 0.72, 0.61. Keywords alone cannot make a TV exclusion airtight:
    /// the measured catch rate is the honest ceiling of this proxy, and 476 shows carry no keyword
    /// at all although hydrated. TV is tagged with genre-name labels far more often than films
    /// (742 shows carry "romance" against 295 films), so these film rates understate TV's.</para>
    ///
    /// <para><b>anime and musical</b> have no genre id on either medium, so nothing can score them:
    /// their lists are the exact labels read off the vocabulary by hand. "based on play or musical"
    /// is deliberately absent — most such titles are adapted plays.</para>
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<Pattern>> Patterns =
        new Dictionary<string, IReadOnlyList<Pattern>>(StringComparer.Ordinal)
        {
            ["romance"] =
            [
                new Word("romance"), new Word("romantic"), new Exact("love"), new Exact("love triangle"),
                new Exact("first love"), new Exact("falling in love"), new Exact("new love"),
                new Exact("love of one's life"), new Exact("forbidden love"), new Exact("boys' love (bl)"),
                new Exact("girls' love (gl)"), new Exact("love at first sight"), new Exact("teenage love"),
                new Exact("young love"), new Exact("secret love"), new Exact("lovesickness"),
                new Exact("love story"), new Exact("lovers"), new Exact("lover"), new Exact("tragic love"),
                new Exact("love letter"), new Exact("true love"), new Exact("soulmates"),
                new Exact("opposites attract"), new Exact("love affair"), new Exact("romcom"), new Exact("hallmark"),
                new Exact("valentine's day"), new Exact("marriage proposal"), new Exact("friends in love"),
                new Exact("pretend relationship"), new Exact("blind date"), new Exact("dating"),
            ],
            ["horror"] =
            [
                new Word("horror"), new Word("slasher"), new Exact("gore"), new Exact("zombie"),
                new Exact("zombie apocalypse"), new Exact("vampire"), new Exact("vampire hunter (slayer)"),
                new Exact("werewolf"), new Exact("demon"), new Exact("demonic"), new Exact("demonic possession"),
                new Exact("possession"), new Exact("possessed"), new Exact("exorcism"), new Exact("exorcist"),
                new Exact("haunted house"), new Exact("haunted"), new Exact("haunting"), new Exact("ghost story"),
                new Exact("vengeful ghost"), new Exact("evil spirit"), new Exact("paranormal"),
                new Exact("paranormal phenomena"), new Exact("paranormal investigation"),
                new Exact("paranormal activity"), new Exact("occult"), new Exact("black magic"),
                new Exact("witchcraft"), new Exact("satanism"), new Exact("satanic cult"),
                new Exact("satanic ritual"), new Exact("séance"), new Exact("ouija board"), new Exact("cannibal"),
                new Exact("cannibalism"), new Exact("undead"), new Exact("dracula"), new Exact("frankenstein"),
                new Exact("creature feature"), new Exact("monster movie"), new Exact("found footage"),
                new Exact("found footage adjacent"), new Exact("evil doll"), new Exact("killer doll"),
                new Exact("possessed doll"), new Exact("killer clown"), new Exact("masked killer"),
                new Exact("urban legend"), new Exact("lovecraftian"), new Exact("cabin in the woods"),
                new Exact("axe murder"), new Exact("decapitation"), new Exact("chainsaw"), new Exact("evil child"),
                new Exact("ancient evil"), new Exact("reanimation"), new Exact("human sacrifice"),
            ],
            ["history"] =
            [
                new Word("historical"), new Exact("history"), new Exact("british monarchy"),
                new Exact("joseon dynasty (1392–1910)"), new Exact("african american history"),
                new Exact("british history"), new Exact("american history"), new Exact("russian history"),
                new Exact("french history"), new Exact("italian history"), new Exact("spanish history"),
                new Exact("european history"), new Exact("soviet union history"), new Exact("world history"),
                new Exact("ancient history"),
            ],
            ["thriller"] =
            [
                new Word("thriller"), new Exact("suspenseful"), new Exact("tense"), new Exact("ominous"),
                new Exact("psychopath"), new Exact("sociopath"), new Exact("neo-noir"), new Exact("home invasion"),
                new Exact("hostage situation"), new Exact("cat and mouse"), new Exact("stalker"),
                new Exact("stalking"), new Exact("manhunt"), new Exact("race against time"),
                new Exact("sadistic psychopath"), new Exact("nordic noir"), new Exact("film noir"),
            ],
            ["music"] =
            [
                new Word("music"), new Exact("concert"), new Exact("concert film"), new Exact("concert footage"),
                new Exact("live performance"), new Exact("rock band"), new Exact("boy band"),
                new Exact("rock 'n' roll"), new Exact("singer-songwriter"), new Exact("recording studio"),
                new Exact("pop star"), new Exact("pop singer"), new Exact("jazz"), new Exact("heavy metal"),
                new Exact("opera"), new Exact("record producer"), new Exact("singing"),
            ],
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
    /// Trakt's own genre slugs — <c>children</c> for family and <c>suspense</c> for thriller — so the
    /// exclusion has to reach them by name.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> Aliases =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["family"] = ["children"],
            ["thriller"] = ["suspense"],
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

        // Exact labels are an array overlap (&&), which Postgres does in C without building a string;
        // whole-word stems need a regular expression, run once per title over its labels joined by
        // newlines (Npgsql translates string.Join over the array to array_to_string and Regex.IsMatch
        // to ~; the pattern means the same to .NET, which the in-memory parity test relies on). A
        // LIKE per pattern per label made the founder's eligibility count five times slower, and a
        // regex for the exact labels too would scan every film for the both-media slugs.
        var title = Expression.Parameter(typeof(Title), "t");
        var keywords = Expression.Property(title, nameof(Title.Keywords));
        var exact = patterns.OfType<Exact>().Select(p => p.Text).ToArray();
        var words = patterns.OfType<Word>().ToList();

        Expression? anyKeyword = null;
        if (exact.Length > 0)
        {
            var k = Expression.Parameter(typeof(string), "k");
            var contains = Expression.Call(ArrayContainsMethod, Expression.Constant(exact), k);
            anyKeyword = Expression.Call(AnyMethod, keywords, Expression.Lambda<Func<string, bool>>(contains, k));
        }

        if (words.Count > 0)
        {
            var joined = Expression.Call(JoinMethod, Expression.Constant(LabelSeparator), keywords);
            var match = Expression.Call(IsMatchMethod, joined, Expression.Constant(WordRegex(words)));
            anyKeyword = anyKeyword is null ? match : Expression.OrElse(anyKeyword, match);
        }

        // Both media ⇒ no media test at all; one ⇒ an equality on MediaType.
        Expression body = media.Count == 2
            ? anyKeyword!
            : Expression.AndAlso(
                Expression.Equal(Expression.Property(title, nameof(Title.MediaType)), Expression.Constant(media[0])),
                anyKeyword!);
        return Expression.Lambda<Func<Title, bool>>(body, title);
    }

    /// <summary>What the labels are joined with before the slug's expression scans them.</summary>
    private const string LabelSeparator = "\n";

    private static readonly MethodInfo JoinMethod =
        typeof(string).GetMethod(nameof(string.Join), [typeof(string), typeof(IEnumerable<string>)])!;

    private static readonly MethodInfo IsMatchMethod =
        typeof(Regex).GetMethod(nameof(Regex.IsMatch), [typeof(string), typeof(string)])!;

    private static readonly MethodInfo AnyMethod = typeof(Enumerable)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(Enumerable.Any) && m.GetParameters().Length == 2)
        .MakeGenericMethod(typeof(string));

    private static readonly MethodInfo ArrayContainsMethod = typeof(Enumerable)
        .GetMethods(BindingFlags.Public | BindingFlags.Static)
        .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
        .MakeGenericMethod(typeof(string));

    /// <summary>
    /// The whole-word stems as one regular expression over newline-joined labels, valid in both .NET
    /// and Postgres (ARE) syntax: a stem is bounded by the start, the end, a newline (a label edge) or
    /// a space (a word edge inside a label).
    /// </summary>
    public static string WordRegex(IEnumerable<Word> words) =>
        $"(^|\\n| )({string.Join('|', words.Select(w => Escape(w.Text)))})( |\\n|$)";

    private static string Escape(string text) =>
        string.Concat(text.Select(c => "\\.^$|?*+()[]{}".Contains(c, StringComparison.Ordinal) ? $"\\{c}" : c.ToString()));
}
