using System.Linq.Expressions;
using System.Text;
using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Persistence;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Application.Search.Retrieval;

/// <summary>
/// The request's hard constraints as one SQL predicate over <see cref="Title"/> — the topic half of
/// every supply count and every facet retrieval. It narrows; it never decides eligibility, which
/// stays in <see cref="EligibilityQueryBuilder"/> and is composed with this by the caller.
///
/// <para>The rules, each chosen so a count quoted to the user is defensible:</para>
/// <list type="bullet">
/// <item>A concept matches by a TMDB keyword that CONTAINS the label from a word start to a word end
/// or an inflection (<see cref="Concepts.KeywordVocabulary.Reaches"/>: "ninja", "female ninja",
/// "ninja warrior", never "alienation" for alien), by one of the USER'S OWN words standing as a whole
/// word in the title's NAME (plural allowed; <see cref="NameLabelsOf"/> — a synonym never matches a
/// name), or by genre slug. A genre slug is matched only as an exclusion of
/// it reads (<see cref="FilterBreaches.ExcludedGenre"/>); a label that restates the concept's genres is
/// dropped, so no keyword or name containing the genre word satisfies a genre. Keywords are stored lower-cased by ingest, so
/// the labels are lower-cased here and the keyword column is not; the name is matched
/// case-insensitively.</item>
/// <item>The keyword match starts at a word boundary because bare containment counted mid-word hits:
/// "heist" matched the keyword "atheist", putting Stigmata and Religulous in the founder's heist
/// supply (13 titles over the eval truth). It ends at a word end or an inflection, so "heists",
/// "zombies" and "time traveler" still match and "alienation" and "assassination" do not.</item>
/// <item>The name arm exists because TMDB's keyword tags are incomplete: "3 Ninjas", "Alien vs.
/// Ninja" and "Batman Ninja vs. Yakuza League" carry no ninja keyword at all, so a keyword-only
/// count told the founder 63 ninja films when the catalogue holds 70. It is a whole-word match, not
/// containment, because names are short prose: containment would count "Ninjababy" (a pregnancy
/// comedy) and "Ninjago" as ninja films, which the eval truth deliberately does not.</item>
/// <item>Concepts AND together; the labels and genres inside one concept OR together. An unresolved
/// concept made only of genre words is read as those genres (<see cref="Read"/>), and a concept that
/// requires exactly what an earlier one does is the same requirement, counted once.</item>
/// <item>A person matches by credit, by resolved id or else by exact case-insensitive name.</item>
/// <item>The year window is inclusive and resolves <c>Year → ReleasedAt → FirstAiredAt</c>; an
/// undated title passes, exactly as the standing year rule treats it.</item>
/// <item>A runtime ceiling needs a KNOWN runtime: "under 90 minutes" is a statement about the title,
/// and a title with no runtime cannot be shown to satisfy it.</item>
/// </list>
/// </summary>
public static class HardConstraintFilter
{
    /// <summary>
    /// The concept labels actually matched for <paramref name="concept"/>: its catalogue labels, or its
    /// own words — minus, on a concept that carries genres, any label made only of genre words
    /// ("comedy", "romantic comedies"). A genre is satisfied by the genre predicate alone, the one
    /// eligibility uses; a keyword or a name that merely CONTAINS the genre word never satisfies it
    /// (HREV1-RET-06: "teen comedy" on a family sci-fi film). That holds when the phrase names more
    /// genres than this concept carries: "romantic comedy" split into a romance concept and a comedy
    /// concept, each of which still drops the phrase.
    /// </summary>
    public static IReadOnlyList<string> LabelsOf(ConceptTerm concept)
    {
        var labels = concept.CatalogueLabels.Count > 0 ? concept.CatalogueLabels : [concept.UserWords];
        var genres = GenresOf(concept);
        return genres.Count == 0
            ? Clean(labels)
            : Clean(labels).Where(label => !RestatesGenres(label, genres)).ToList();
    }

    /// <summary>A label shorter than this is never matched in a name (see <see cref="NameLabelsOf"/>).</summary>
    public const int MinNameLabelLength = 3;

    /// <summary>
    /// The labels the NAME arm matches for <paramref name="concept"/>: those of <see cref="LabelsOf"/>
    /// the user actually said — standing in their words as a whole word or phrase, a plural or a
    /// singular of one ("ninjas" says "ninja", "robberies" says "robbery") — and of at least
    /// <see cref="MinNameLabelLength"/> letters. A synonym answers a request only as a keyword: it is a
    /// curated claim about TMDB's tags, not about titles' prose, and in names "ronin" counted Ronin
    /// (1998) as a samurai film the founder had seen, "trial" Maze Runner: The Scorch Trials as a
    /// courtroom drama, "volcano" Joe Versus the Volcano as a disaster film and "ai" the Italian
    /// preposition in Vacanze ai Caraibi (HREV3-RET-A). The user's own two-letter "AI" fails the same way,
    /// so it too is matched by keyword only. A word of the user's phrase is their word too ("alien
    /// invasion movies" says "alien"; "ai robot" says "robot").
    ///
    /// <para>When the user's words are not known (<see cref="ConceptTerm.StandInWords"/>: the planner's
    /// keywords joined, for a state rebuilt from a v1 reading or a v1 topic no word of the user's
    /// anchored), the name arm reads every label that is not a curated synonym
    /// (<see cref="Concepts.ConceptSynonyms.IsSynonym"/>): [samurai, ronin] names "samurai" and never
    /// "ronin". Reading none of them instead lost every title found by its name alone — 63 of the
    /// founder's 70 ninja movies — and reading them all counted Ronin (1998) again.</para>
    /// </summary>
    public static IReadOnlyList<string> NameLabelsOf(ConceptTerm concept)
    {
        ArgumentNullException.ThrowIfNull(concept);
        if (concept.StandInWords)
        {
            return LabelsOf(concept)
                .Where(label => Concepts.ConceptResolver.Normalise(label) is { Length: >= MinNameLabelLength } normal
                    && !Concepts.ConceptSynonyms.IsSynonym(normal))
                .ToList();
        }

        var said = SaidForms(concept.UserWords);
        if (said.Count == 0)
        {
            return [];
        }

        return LabelsOf(concept)
            .Where(label => Concepts.ConceptResolver.Normalise(label) is { Length: >= MinNameLabelLength } normal
                && said.Any(form => NameMatches(form, normal)))
            .ToList();
    }

    /// <summary>The user's words normalised, and the same with any one word read as its singular.</summary>
    private static List<string> SaidForms(string? words)
    {
        var normal = Concepts.ConceptResolver.Normalise(words);
        if (normal.Length == 0)
        {
            return [];
        }

        var tokens = normal.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var forms = new List<string> { normal };
        for (var i = 0; i < tokens.Length; i++)
        {
            foreach (var singular in Concepts.ConceptSynonyms.Singulars(tokens[i]))
            {
                var copy = (string[])tokens.Clone();
                copy[i] = singular;
                forms.Add(string.Join(' ', copy));
            }
        }

        return forms;
    }

    /// <summary>True when <paramref name="label"/> is made only of genre words and names at least one of <paramref name="genres"/>.</summary>
    private static bool RestatesGenres(string label, IReadOnlyList<string> genres) =>
        Concepts.ConceptSynonyms.GenresNamedBy(Concepts.ConceptResolver.Normalise(label)) is { } named
        && named.Any(g => genres.Contains(g, StringComparer.Ordinal));

    /// <summary>The genre slugs that satisfy <paramref name="concept"/>, cleaned.</summary>
    public static IReadOnlyList<string> GenresOf(ConceptTerm concept) => Clean(concept.Genres);

    /// <summary>
    /// The concepts as the predicate reads them: a concept no resolver produced has its genre words
    /// read as genres (<see cref="Concepts.ConceptResolver.GenreReading"/>), and a concept requiring
    /// exactly what an earlier one does is dropped (<see cref="Concepts.ConceptResolver.Merge"/>). The
    /// stored conversation state holds UNRESOLVED concepts, and counting "romantic" as keywords
    /// containing the word, AND-ed with "romance", told the founder there were 94 romantic shows when
    /// 1,306 are romance by the rule his exclusion applies (HREV2-RET-03).
    /// </summary>
    public static IReadOnlyList<ConceptTerm> Read(IReadOnlyList<ConceptTerm> concepts)
    {
        ArgumentNullException.ThrowIfNull(concepts);
        var read = new List<ConceptTerm>(concepts.Count);
        foreach (var concept in concepts)
        {
            read.AddRange(Concepts.ConceptResolver.GenreReading(concept) ?? [concept]);
        }

        return Concepts.ConceptResolver.Merge(read);
    }

    /// <summary><paramref name="titles"/> narrowed to those satisfying every hard constraint.</summary>
    public static IQueryable<Title> Apply(IQueryable<Title> titles, IReelDbContext db, HardConstraints hard)
    {
        var query = hard.Media switch
        {
            MediaScope.Movies => titles.Where(t => t.MediaType == MediaType.Movie),
            MediaScope.Shows => titles.Where(t => t.MediaType == MediaType.Show),
            _ => titles,
        };

        foreach (var concept in Read(hard.Concepts))
        {
            query = query.Where(ConceptPredicate(concept));
        }

        foreach (var person in hard.People)
        {
            query = query.Where(PersonPredicate(db, person));
        }

        if (hard.Years is { } years && (years.From is not null || years.To is not null))
        {
            var from = years.From ?? int.MinValue;
            var to = years.To ?? int.MaxValue;
            query = query.Where(t =>
                (t.Year == null && t.ReleasedAt == null && t.FirstAiredAt == null)
                || ((t.Year ?? (t.ReleasedAt != null ? t.ReleasedAt.Value.Year : t.FirstAiredAt!.Value.Year)) >= from
                    && (t.Year ?? (t.ReleasedAt != null ? t.ReleasedAt.Value.Year : t.FirstAiredAt!.Value.Year)) <= to));
        }

        if (hard.MaxRuntimeMinutes is { } ceiling)
        {
            query = query.Where(t => t.RuntimeMinutes != null && t.RuntimeMinutes <= ceiling);
        }

        return query;
    }

    /// <summary>
    /// The evidence strings a title that passed <see cref="Apply"/> carries for the request's concepts
    /// and people (<c>keyword:&lt;title keyword&gt;</c>, <c>genre:&lt;slug&gt;</c>,
    /// <c>person:&lt;name&gt;/&lt;role&gt;</c>), computed in memory from the materialised title so the
    /// strings name the actual tag that matched.
    /// </summary>
    public static IReadOnlyList<string> EvidenceFor(Title title, HardConstraints hard)
    {
        var evidence = new List<string>();
        foreach (var concept in Read(hard.Concepts))
        {
            var labels = LabelsOf(concept);
            var exact = concept.ExactLabels is { } set ? set.ToHashSet(StringComparer.Ordinal) : null;
            foreach (var keyword in title.Keywords)
            {
                if (exact is not null ? exact.Contains(keyword) : labels.Any(l => KeywordMatches(keyword, l)))
                {
                    evidence.Add($"keyword:{keyword}");
                }
            }

            foreach (var label in NameLabelsOf(concept))
            {
                if (NameMatches(title.Name, label))
                {
                    evidence.Add($"name:{label}");
                }
            }

            foreach (var genre in GenresOf(concept))
            {
                if (GenreStandIns.ExcludedBy(title, genre))
                {
                    evidence.Add($"genre:{genre}");
                }
            }
        }

        // Only titles that passed the person predicate are ever passed in, so each credited person is
        // evidence by construction; the role is the one the request asked about.
        foreach (var person in hard.People)
        {
            evidence.Add($"person:{person.Name.Trim()}/{person.Role.ToString().ToLowerInvariant()}");
        }

        return evidence.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The Postgres (ARE) pattern the name arm matches <paramref name="label"/> with: the label as a
    /// whole word or phrase, optionally pluralised (<c>\mninja(e?s)?\M</c> matches "3 Ninjas" and
    /// "Alien vs. Ninja", not "Ninjababy" or "Ninjago"). Regex metacharacters in the label are escaped,
    /// so a label is always matched literally.
    /// </summary>
    internal static string NamePattern(string label) => $@"\m{EscapeLiteral(label)}(e?s)?\M";

    /// <summary>
    /// The Postgres (ARE) pattern the keyword arm matches <paramref name="label"/> with: the label
    /// starting at a word boundary and ending at one, directly or after an inflection — a plural only
    /// for a short label (<c>\mheist(e?s|e?rs?|ings?|e?d)?\M</c> matches "heist", "heists" and "heist gone
    /// wrong", not "atheist"; <c>\malien…\M</c> not "alienation") — metacharacters escaped. A label
    /// ending in punctuation keeps only its word start: <c>\M</c> needs a word character before it.
    /// </summary>
    internal static string KeywordPattern(string label)
    {
        if (label.Length == 0 || !char.IsLetterOrDigit(label[^1]) && label[^1] != '_')
        {
            return $@"\m{EscapeLiteral(label)}";
        }

        var ending = label.Length <= Concepts.KeywordVocabulary.WholeWordStemLength
            ? Concepts.KeywordVocabulary.PluralPattern
            : Concepts.KeywordVocabulary.InflectionPattern;

        // A very short label is no word of a hyphenated compound ("shoujo-ai"): a lookbehind instead of \m.
        var start = label.Length <= Concepts.KeywordVocabulary.CompoundBlindStemLength ? "(?<![[:alnum:]_-])" : @"\m";
        return $@"{start}{EscapeLiteral(label)}{ending}\M";
    }

    /// <summary>
    /// The in-memory twin of <see cref="KeywordPattern"/>, for evidence — the vocabulary's own reach
    /// (<see cref="Concepts.KeywordVocabulary.Reaches"/>), so the regex fallback, the exact labels and
    /// the evidence read one rule.
    /// </summary>
    internal static bool KeywordMatches(string? keyword, string label) =>
        !string.IsNullOrEmpty(keyword)
        && label.Length > 0
        && Concepts.KeywordVocabulary.Reaches(keyword, label, label.Length <= Concepts.KeywordVocabulary.WholeWordStemLength);

    /// <summary>
    /// The in-memory twin of <see cref="NamePattern"/>, for evidence: true when <paramref name="label"/>
    /// stands in <paramref name="name"/> as a whole word or phrase, optionally pluralised. A word
    /// character is a letter, a digit or an underscore, as it is for Postgres's <c>\m</c> and <c>\M</c>.
    /// </summary>
    internal static bool NameMatches(string? name, string label) =>
        !string.IsNullOrEmpty(name)
        && label.Length > 0
        && Regex.IsMatch(
            name,
            $@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(label)}(e?s)?(?![\p{{L}}\p{{N}}_])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));

    private static Expression<Func<Title, bool>> ConceptPredicate(ConceptTerm concept)
    {
        var arms = new List<Expression<Func<Title, bool>>>();
        if (concept.ExactLabels is { } exact)
        {
            // Resolved concept: the keyword arm is an overlap with exactly the vocabulary labels the
            // stems reach — the GIN index on "Keywords" answers it (M2) instead of a regex over every
            // title's labels. The holder makes the labels a parameter, which Npgsql turns into "&&".
            if (exact.Count > 0)
            {
                var holder = new LabelHolder([.. exact]);
                arms.Add(t => t.Keywords.Any(k => holder.Labels.Contains(k)));
            }
        }

        // Translated by Npgsql to k ~ @keywordPattern over unnest("Keywords"), and "Name" ~* @namePattern
        // (see the class remarks for why each arm is bounded where it is). Each regex sits behind a
        // plain strpos guard that implies nothing new (the label is a substring of any match) but
        // lets Postgres skip the regex engine for almost every row: unguarded, the three heist
        // labels cost ~440 ms over the 38k-title catalogue; guarded, ~70 ms, as containment did.
        if (concept.ExactLabels is null)
        {
            foreach (var label in LabelsOf(concept))
            {
                var value = label;
                var keywordPattern = KeywordPattern(label);
                arms.Add(t => t.Keywords.Any(k => k.Contains(value) && Regex.IsMatch(k, keywordPattern)));
            }
        }

        // The name arm reads only the user's own words: a synonym is a keyword claim (NameLabelsOf).
        foreach (var label in NameLabelsOf(concept))
        {
            var value = label;
            var namePattern = NamePattern(label);
            arms.Add(t => t.Name.ToLower().Contains(value) && Regex.IsMatch(t.Name, namePattern, RegexOptions.IgnoreCase));
        }

        // A genre concept matches what an exclusion of that genre would remove: the tag, its alias
        // genres, the anime composite and, on the medium whose TMDB taxonomy cannot tag the genre,
        // its stand-in keywords — so "horror shows" reaches the TV horror that TMDB files under
        // keywords (14 shows carry the genre; hundreds carry the labels).
        foreach (var genre in GenresOf(concept))
        {
            arms.Add(FilterBreaches.ExcludedGenre(genre));
        }

        // A concept with nothing to match on matches nothing: an empty concept is a resolver defect,
        // and reading it as "anything" would answer a topical request with the whole catalogue.
        if (arms.Count == 0)
        {
            return t => false;
        }

        var parameter = Expression.Parameter(typeof(Title), "t");
        var body = arms
            .Select(arm => new ParameterSwap(arm.Parameters[0], parameter).Visit(arm.Body))
            .Aggregate(Expression.OrElse);
        return Expression.Lambda<Func<Title, bool>>(body, parameter);
    }

    /// <summary>
    /// A SOFT match (a mood, never a requirement): a keyword overlapping <paramref name="exactLabels"/>
    /// or a genre in <paramref name="genres"/> as an exclusion of it reads (stand-ins included). No
    /// name arm: a mood is not a word to find in titles' names ("Small Town Crime" is not cosy). Null
    /// when there is nothing to match on, so the caller skips the query rather than reading "anything".
    /// </summary>
    public static Expression<Func<Title, bool>>? SoftPredicate(IReadOnlyCollection<string> exactLabels, IReadOnlyList<string> genres)
    {
        var arms = new List<Expression<Func<Title, bool>>>();
        if (exactLabels.Count > 0)
        {
            var holder = new LabelHolder([.. exactLabels]);
            arms.Add(t => t.Keywords.Any(k => holder.Labels.Contains(k)));
        }

        foreach (var genre in Clean(genres))
        {
            arms.Add(FilterBreaches.ExcludedGenre(genre));
        }

        if (arms.Count == 0)
        {
            return null;
        }

        var parameter = Expression.Parameter(typeof(Title), "t");
        var body = arms
            .Select(arm => new ParameterSwap(arm.Parameters[0], parameter).Visit(arm.Body))
            .Aggregate(Expression.OrElse);
        return Expression.Lambda<Func<Title, bool>>(body, parameter);
    }

    private static Expression<Func<Title, bool>> PersonPredicate(IReelDbContext db, PersonConstraint person)
    {
        var anyRole = person.Role == PersonRole.Any;
        var role = person.Role == PersonRole.Director ? CreditRole.Director : CreditRole.Actor;
        if (person.PersonId is { } personId)
        {
            return t => db.TitleCredits.Any(c =>
                c.TitleId == t.Id && c.PersonId == personId && (anyRole || c.Role == role));
        }

        var name = person.Name.Trim().ToLowerInvariant();
        return t => db.TitleCredits.Any(c =>
            c.TitleId == t.Id
            && (anyRole || c.Role == role)
            && db.Persons.Any(p => p.Id == c.PersonId && p.Name.ToLower() == name));
    }

    /// <summary>
    /// Escapes the characters a Postgres advanced regular expression treats as special. Only ASCII
    /// punctuation is escaped: a backslash before a letter or digit would start an ARE escape.
    /// </summary>
    private static string EscapeLiteral(string label)
    {
        var escaped = new StringBuilder(label.Length * 2);
        foreach (var c in label)
        {
            if (c is '\\' or '^' or '$' or '.' or '|' or '?' or '*' or '+' or '(' or ')' or '[' or ']' or '{' or '}')
            {
                escaped.Append('\\');
            }

            escaped.Append(c);
        }

        return escaped.ToString();
    }

    private static List<string> Clean(IEnumerable<string> values) =>
        values
            .Select(v => v.Trim().ToLowerInvariant())
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>A closure-shaped holder so EF Core sends the exact labels as one array parameter.</summary>
    private sealed class LabelHolder(string[] labels)
    {
        public readonly string[] Labels = labels;
    }

    /// <summary>Rebinds one lambda's parameter onto another so OR-ed arms share a single <c>t</c>.</summary>
    private sealed class ParameterSwap(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : node;
    }
}
