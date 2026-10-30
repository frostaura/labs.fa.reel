using FrostAura.Reel.Application.Search;
using FrostAura.Reel.Application.Search.AskReel;
using FrostAura.Reel.Application.Search.AskReel.Engine;
using FrostAura.Reel.Application.Search.Retrieval;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Tests.Search;

/// <summary>
/// HREV3-ASK-E: a plan no model wrote reads a mood as a mood and a year phrase as a year window. With
/// the planner down, "tense heist movies from the last ten years" searched the hard topics "tense
/// heist" and "ten years", the supply read 0, and three unrelated titles were shown. Pinned over the
/// golden set's first-turn messages that carry a mood, a year phrase or filler, through BOTH fallbacks
/// (the planner failing, and no model at all), at a fixed current year.
/// </summary>
public class AskReelKeylessRequestTests
{
    private const int Now = 2026;

    [Theory]
    [InlineData("tense heist movies from the last ten years", new[] { "heist" }, new[] { "tense" }, 2016, 2026)]
    [InlineData("heist movies from the last 10 years", new[] { "heist" }, new string[0], 2016, 2026)]
    // HREV4-RET-02: a genre word ending a topic phrase is the genre the topic sits in.
    [InlineData("a tense heist thriller from the 2010s, nothing too long", new[] { "heist" }, new[] { "tense" }, 2010, 2019, "thriller")]
    // HREV5-ASK-01: a hyphenated genre word is the genre, never the topic "sci".
    [InlineData("80s sci-fi movies", new string[0], new string[0], 1980, 1989, "science-fiction")]
    [InlineData("a cozy feel-good movie", new string[0], new[] { "cozy", "feel good" }, null, null)]
    [InlineData("dark gritty heist, no superheroes", new[] { "heist" }, new[] { "dark", "gritty" }, null, null)]
    [InlineData("quiet character dramas", new[] { "character" }, new[] { "quiet" }, null, null, "drama")]
    [InlineData("something genuinely funny", new string[0], new[] { "funny" }, null, null)]
    [InlineData("romantic comedies", new[] { "romantic comedies" }, new string[0], null, null)]
    [InlineData("good samurai films before 2000", new[] { "samurai" }, new string[0], null, 1999)]
    [InlineData("zombie movies since 2015", new[] { "zombie" }, new string[0], 2015, null)]
    [InlineData("war movies from 1940 to 1945", new[] { "war" }, new string[0], 1940, 1945)]
    [InlineData("spy thrillers, nothing from the 80s", new[] { "spy" }, new string[0], null, null, "thriller")]
    [InlineData("time travel movies", new[] { "time travel" }, new string[0], null, null)]
    [InlineData("heist movies", new[] { "heist" }, new string[0], null, null)]
    public void A_plan_no_model_wrote_reads_moods_and_years_as_what_they_are(
        string message, string[] concepts, string[] moods, int? from, int? to, string? genre = null)
    {
        var expectedYears = from is null && to is null ? null : new YearRange(from, to);
        var failed = PlanResult.Failed(new PlanFailure(AgentFailureKind.TimedOut, null, false), LlmCallMeta.None);
        foreach (var plan in new[]
                 {
                     FallbackPlans.CarriedForward(failed, message, ConversationState.Empty, Now),
                     FallbackPlans.Keyless(message, ConversationState.Empty, Now),
                 })
        {
            Assert.Equal(concepts, plan.Delta.Concepts);
            Assert.Equal(moods, plan.Delta.Moods);
            Assert.Equal(expectedYears, plan.Delta.Years);
            Assert.Equal(genre is null ? [] : [genre], plan.Delta.Genres);
        }
    }

    /// <summary>
    /// A carried-forward follow-up keeps adding only soft words — and a year phrase in it narrows the
    /// years rather than becoming three moods ("last", "five", "years").
    /// </summary>
    [Fact]
    public void A_carried_forward_follow_up_reads_its_year_phrase_as_years_not_moods()
    {
        var state = ConversationState.Empty with
        {
            Version = 2,
            Hard = HardConstraints.None with { Concepts = [new ConceptTerm("heist", [], [])] },
        };
        var failed = PlanResult.Failed(new PlanFailure(AgentFailureKind.Truncated, null, false), LlmCallMeta.None);

        var plan = FallbackPlans.CarriedForward(failed, "from the past five years, funnier", state, Now);

        Assert.Equal(TurnAct.Refine, plan.Act);
        Assert.Empty(plan.Delta.Concepts);
        Assert.Equal(new YearRange(2021, 2026), plan.Delta.Years);
        // A comparison is an attribute steer (HREV4-ASK-05), never a mood or a topic.
        Assert.Empty(plan.Delta.Moods);
        Assert.Equal([new AttributeDirection("humor", AttributeTrend.More)], plan.Delta.Directions);
    }

    /// <summary>
    /// HREV4-ASK-05 / HREV4-RET-02: the model-less reader on the follow-ups and shapes that misread —
    /// through BOTH fallbacks, in a conversation that has shown cards. A question about a card is an
    /// item question; "like &lt;title&gt;" is a reference; comparisons are steers; connectives and
    /// generic nouns are never topics; a negation is the whole phrase; a genre ending a phrase is a genre.
    /// </summary>
    [Theory]
    [InlineData("is the first one violent?", TurnAct.ItemQuestion, "", "", "", "", "", null)]
    [InlineData("how long is the second one?", TurnAct.ItemQuestion, "", "", "", "", "", null)]
    [InlineData("is Heat violent?", TurnAct.ItemQuestion, "", "", "", "", "", null)]
    [InlineData("more like the first one", TurnAct.Refine, "", "", "", "", "", null)]
    [InlineData("like John Wick but funnier", TurnAct.NewRequest, "", "", "", "john wick", "humor:More", null)]
    [InlineData("movies like Inception", TurnAct.NewRequest, "", "", "", "inception", "", MediaScope.Movies)]
    [InlineData("something similar to Heat but darker", TurnAct.NewRequest, "", "", "", "heat", "darkness:More", null)]
    [InlineData("I'd like a heist movie", TurnAct.NewRequest, "heist", "", "", "", "", MediaScope.Movies)]
    [InlineData("older stuff is fine", TurnAct.Refine, "", "", "", "", "", null)]
    [InlineData("what about movies instead", TurnAct.Refine, "", "", "", "", "", MediaScope.Movies)]
    [InlineData("shows instead", TurnAct.Refine, "", "", "", "", "", MediaScope.Shows)]
    [InlineData("love stories", TurnAct.NewRequest, "", "romance", "", "", "", null)]
    [InlineData("no walking dead", TurnAct.Refine, "", "", "walking dead", "", "", null)]
    [InlineData("zombie shows, no walking dead or vampires", TurnAct.NewRequest, "zombie", "", "walking dead|vampires", "", "", MediaScope.Shows)]
    [InlineData("spy thrillers", TurnAct.NewRequest, "spy", "thriller", "", "", "", null)]
    [InlineData("true crime documentaries", TurnAct.NewRequest, "true crime", "documentary", "", "", "", null)]
    [InlineData("romantic comedies", TurnAct.NewRequest, "romantic comedies", "", "", "", "", null)]
    [InlineData("time travel movies", TurnAct.NewRequest, "time travel", "", "", "", "", MediaScope.Movies)]
    // HREV5-RET-02: a title that starts with "The" is a name, never the pointer "the …".
    [InlineData("movies like the matrix", TurnAct.NewRequest, "", "", "", "the matrix", "", MediaScope.Movies)]
    [InlineData("shows like the office", TurnAct.NewRequest, "", "", "", "the office", "", MediaScope.Shows)]
    [InlineData("something like The Godfather but darker", TurnAct.NewRequest, "", "", "", "the godfather", "darkness:More", null)]
    // HREV5-ASK-04 residual: an ordinal inside a topic is part of the topic.
    [InlineData("is there a first contact movie?", TurnAct.NewRequest, "first contact", "", "", "", "", MediaScope.Movies)]
    [InlineData("are there any good second world war films?", TurnAct.NewRequest, "second world war", "", "", "", "", MediaScope.Movies)]
    public void The_model_less_reader_reads_follow_ups_references_comparisons_and_negations_as_what_they_are(
        string message, TurnAct act, string concepts, string genres, string negations, string references, string directions, MediaScope? media)
    {
        var state = ConversationState.Empty with
        {
            Version = 2,
            Hard = HardConstraints.None with { Concepts = [new ConceptTerm("zombie", [], [])], Media = MediaScope.Shows },
            LastPresented =
            [
                new PresentedRef(Guid.NewGuid(), new TurnHandle(2, 1), "Heat", 1995, Domain.Catalog.MediaType.Movie),
                new PresentedRef(Guid.NewGuid(), new TurnHandle(2, 2), "Ronin", 1998, Domain.Catalog.MediaType.Movie),
            ],
        };
        static string[] List(string text) => text.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var failed = PlanResult.Failed(new PlanFailure(AgentFailureKind.TimedOut, null, false), LlmCallMeta.None);
        var keyless = FallbackPlans.Keyless(message, state, Now);
        var carried = FallbackPlans.CarriedForward(failed, message, state, Now);

        Assert.Equal(act, keyless.Act);
        Assert.Equal(List(concepts), keyless.Delta.Concepts);
        Assert.Equal(List(genres), keyless.Delta.Genres);
        Assert.Equal(List(negations), keyless.Delta.Negations);
        Assert.Equal(List(references), keyless.Delta.ReferenceNames);
        Assert.Equal(List(directions), keyless.Delta.Directions.Select(d => $"{d.Attribute}:{d.Trend}"));
        Assert.Equal(media, keyless.Delta.Media);

        // The carried-forward follow-up reads the same words; on a later turn its topics are soft only.
        Assert.Equal(act == TurnAct.ItemQuestion ? TurnAct.ItemQuestion : TurnAct.Refine, carried.Act);
        Assert.Empty(carried.Delta.Concepts);
        Assert.Equal(List(negations), carried.Delta.Negations);
        Assert.Equal(List(references), carried.Delta.ReferenceNames);
        Assert.Equal(List(directions), carried.Delta.Directions.Select(d => $"{d.Attribute}:{d.Trend}"));
        foreach (var plan in new[] { keyless, carried })
        {
            Assert.DoesNotContain(plan.Delta.Concepts.Concat(plan.Delta.Moods), w =>
                w is "funnier" or "older" or "fine" or "instead" or "stories" or "first" or "violent" or "inception" or "john wick");
            if (act == TurnAct.ItemQuestion)
            {
                Assert.Equal([AskTool.TitleFacts], plan.Tools.Select(t => t.Tool));
            }
        }
    }

    /// <summary>
    /// HREV5-RET-02: "the" opened the pointer words that stop "like the second one" being read as a
    /// title, and so it also stopped every title that starts with "The" — "movies like the matrix"
    /// replied "There are no matrix movies left for you". A "the" followed by a name is part of the
    /// name; followed by a pointer or filler ("the second one", "the other one", "the rest") it is not.
    /// </summary>
    [Theory]
    [InlineData("movies like the matrix", "the matrix")]
    [InlineData("like The Office but darker", "the office")]
    [InlineData("something similar to the thing", "the thing")]
    [InlineData("shows like the wire, no cops", "the wire")]
    [InlineData("like Heat", "heat")]
    [InlineData("more like the second one", null)]
    [InlineData("something like the last one", null)]
    [InlineData("like the other one", null)]
    [InlineData("more like the same", null)]
    [InlineData("something like the rest", null)]
    [InlineData("like the one before", null)]
    [InlineData("like the others", null)]
    [InlineData("more like the first film", null)]
    public void A_title_that_starts_with_the_is_a_name_and_a_pointer_is_not(string message, string? expected) =>
        Assert.Equal(expected, FallbackReader.ReferenceName(message));

    /// <summary>
    /// The keyless reader dropped every ordinal as a pointer word, so "is there a first contact movie?"
    /// counted "contact movies" and "good second world war films" counted "world war movies". An ordinal
    /// is a pointer only before a card noun ("the second one", "the third film"); before a topic word it
    /// is part of the topic — never before a genre word ("my first horror film" is horror).
    /// </summary>
    [Theory]
    [InlineData("is there a first contact movie?", "first contact", "")]
    [InlineData("are there any good second world war films?", "second world war", "")]
    [InlineData("third reich documentaries", "third reich", "documentary")]
    [InlineData("first time travel movie you have", "time travel", "")]
    [InlineData("my first horror film", "horror", "")]
    [InlineData("more like the first one", "", "")]
    [InlineData("the second movie you showed", "", "")]
    public void An_ordinal_inside_a_topic_is_part_of_the_topic(string message, string concepts, string genres)
    {
        static string[] List(string text) => text.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var reading = FallbackReader.Read(message, ConversationState.Empty, Now);

        Assert.Equal(List(concepts), reading.Concepts);
        Assert.Equal(List(genres), reading.Genres);
    }

    /// <summary>
    /// When the user wants to watch is never what they want to watch: with no model, "something funny
    /// tonight" searched the topic "tonight" and replied "There are no tonight titles left for you".
    /// </summary>
    [Theory]
    [InlineData("something funny tonight", "")]
    [InlineData("a heist movie for tonight", "heist")]
    [InlineData("zombie films to watch this evening", "zombie")]
    [InlineData("something for today", "")]
    [InlineData("a samurai film for tomorrow", "samurai")]
    public void A_when_word_is_never_a_topic(string message, string concepts)
    {
        static string[] List(string text) => text.Split('|', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(List(concepts), FallbackReader.Read(message, ConversationState.Empty, Now).Concepts);
        Assert.Equal(List(concepts), AskReelIntentSanitizer.ReadKeylessRequest(message, Now).Phrases);
    }

    /// <summary>A first-turn carried-forward plan keeps its topics hard, exactly as the keyless one does.</summary>
    [Theory]
    [InlineData("spy thrillers", "spy", "thriller")]
    [InlineData("love stories", "", "romance")]
    [InlineData("found footage horror", "found footage", "horror")]
    public void A_first_turn_fallback_keeps_the_reading_hard(string message, string concepts, string genres)
    {
        var failed = PlanResult.Failed(new PlanFailure(AgentFailureKind.TimedOut, null, false), LlmCallMeta.None);
        static string[] List(string text) => text.Split('|', StringSplitOptions.RemoveEmptyEntries);
        foreach (var plan in new[] { FallbackPlans.CarriedForward(failed, message, ConversationState.Empty, Now), FallbackPlans.Keyless(message, ConversationState.Empty, Now) })
        {
            Assert.Equal(TurnAct.NewRequest, plan.Act);
            Assert.Equal(List(concepts), plan.Delta.Concepts);
            Assert.Equal(List(genres), plan.Delta.Genres);
        }
    }

    /// <summary>
    /// HREV4-ASK-04: v1's degraded reading (the interpreter's heuristic) drops the floor word inside a
    /// topic; its keywords are widened back to the phrase the user wrote. Genre words stay genres.
    /// </summary>
    [Theory]
    [InlineData("time travel movies", "travel", "time travel")]
    [InlineData("time loop movies", "loop", "time loop")]
    [InlineData("found footage horror", "footage", "found footage")]
    [InlineData("shark horrors", "shark", "shark")]
    [InlineData("spy thrillers", "spy", "spy")]
    [InlineData("Liam Neeson movies", "liam|neeson", "liam neeson")]
    [InlineData("heist movies", "heist", "heist")]
    [InlineData("ninja movies", "samurai", "samurai")]
    [InlineData("is there a first contact movie?", "contact", "first contact")]
    public void A_degraded_v1_topic_is_the_users_whole_phrase(string message, string heuristic, string expected) =>
        Assert.Equal(
            expected.Split('|'),
            AskReelDegradedTopic.WholePhrases(heuristic.Split('|'), message, Now));

    [Theory]
    [InlineData("movies from last year", 2025, 2025)]
    [InlineData("the last decade of horror", 2016, 2026)]
    [InlineData("the '90s thrillers", 1990, 1999)]
    [InlineData("00s comedies", 2000, 2009)]
    [InlineData("heist films in 2019", 2019, 2019)]
    [InlineData("heist films after 2015", 2016, null)]
    public void Every_written_year_window_is_read(string message, int? from, int? to)
    {
        var request = AskReelIntentSanitizer.ReadKeylessRequest(message, Now);

        Assert.Equal(new YearRange(from, to), request.Years);
        Assert.DoesNotContain(request.Phrases, p => p.Any(char.IsDigit));
    }

    /// <summary>
    /// HREV5-ASK-01: with the planner failing twice (or no model at all), "action movies but nothing
    /// with superheroes" planned the concepts [action, superheroes], replied "I searched for action
    /// superheroes" and showed Black Panther and Birds of Prey — "nothing WITH" closed the negation
    /// before its object. "sci-fi without aliens" read the topic "sci", and "war movies but not World
    /// War II" lost "war" to the narrower negation. A word a cue governs is a negation, and never also
    /// a topic; a negation of something narrower never removes the broader thing asked for. Varied over
    /// where the "with" stands, over a cue that is not "nothing", and over a later request that does win.
    /// </summary>
    [Theory]
    [InlineData("action movies but nothing with superheroes", "action", "", "superheroes")]
    [InlineData("comedies, nothing with zombies", "comedies", "", "zombies")]
    [InlineData("sci-fi without aliens", "", "science-fiction", "aliens")]
    [InlineData("war movies but not World War II", "war", "", "world war")]
    [InlineData("thrillers except ones with sharks", "thrillers", "", "sharks")]
    [InlineData("rom-com, nothing with weddings", "", "romance|comedy", "weddings")]
    [InlineData("heist movies with no gore", "heist", "", "gore")]
    [InlineData("no sci-fi, just westerns", "westerns", "", "sci-fi")]
    // The latest mention still decides when it is the same thing: asked for after, it is asked for;
    // ruled out on its own after, it is ruled out.
    [InlineData("no zombies. actually zombie comedies", "zombie", "comedy", "")]
    [InlineData("ninja movies, no ninja", "", "", "ninja")]
    // Round 7: "but" after "anything" means "except", and a list of what is ruled out stays ruled out
    // past "and" — varied over the head, the joining word, a "with" before the second object, and a
    // list that ends where a request for something else begins.
    [InlineData("anything but superheroes", "", "", "superheroes")]
    [InlineData("show me anything but superheroes", "", "", "superheroes")]
    [InlineData("everything but horror please", "", "", "horror")]
    [InlineData("any film but a western", "", "", "western")]
    [InlineData("comedies other than romcoms", "comedies", "", "romcoms")]
    [InlineData("action movies but nothing with superheroes and aliens", "action", "", "superheroes|aliens")]
    [InlineData("action movies but nothing with superheroes or aliens", "action", "", "superheroes|aliens")]
    [InlineData("heist films, nothing with zombies and with vampires", "heist", "", "zombies|vampires")]
    [InlineData("no gore and something about heists", "heists", "", "gore")]
    [InlineData("no zombies and no vampires", "", "", "zombies|vampires")]
    // HREV6-ASK-01: the cues the list did not hold — "excluding", "apart from", "sans", "sick of" and
    // a "no thanks" that refuses the clause BEFORE it. Varied over the cue's neighbours (a "with" after
    // it, a clause after it), over the refusal's punctuation, and over the other words of each family.
    [InlineData("heist movies excluding anything with guns", "heist", "", "guns")]
    [InlineData("horror movies apart from slashers", "horror", "", "slashers")]
    [InlineData("superhero movies? no thanks, give me westerns", "westerns", "", "superhero")]
    [InlineData("I'm sick of superheroes, give me westerns", "westerns", "", "superheroes")]
    [InlineData("action movies sans superheroes", "action", "", "superheroes")]
    [InlineData("spy thrillers excluding James Bond", "spy", "thriller", "james bond")]
    [InlineData("comedies aside from romcoms", "comedies", "", "romcoms")]
    [InlineData("I'm tired of zombies, something with dragons", "dragons", "", "zombies")]
    [InlineData("fed up with superheroes, westerns please", "westerns", "", "superheroes")]
    [InlineData("I've had enough of vampires. heist films", "heist", "", "vampires")]
    [InlineData("superhero movies no thanks", "", "", "superhero")]
    [InlineData("superhero movies? no thank you. westerns", "westerns", "", "superhero")]
    [InlineData("vampire shows? nah, westerns", "westerns", "", "vampire")]
    [InlineData("superheroes? not that, westerns please", "westerns", "", "superheroes")]
    [InlineData("heist films excluding Heat", "heist", "", "heat")]
    // HREV6-ASK-01-R1: a bare "no" or "not for me" refusing the clause before it, and the phrase cues
    // the list missed ("sick to death of", "had it with", "done with") — each left the ruled-out topic,
    // or the cue's own words, required. Varied over the punctuation, the refusal and the cue's lead-in;
    // a "no" that opens the message refuses nothing before it, and "done with" governs only when said
    // of the user ("so done with"), never of a film ("done with practical effects").
    [InlineData("superhero movies? no, give me westerns", "westerns", "", "superhero")]
    [InlineData("superhero movies? no. westerns", "westerns", "", "superhero")]
    [InlineData("superhero movies, not for me. westerns", "westerns", "", "superhero")]
    [InlineData("vampire shows? pass. heist films", "heist", "", "vampire")]
    [InlineData("sick to death of superheroes, give me westerns", "westerns", "", "superheroes")]
    [InlineData("I've had it with zombies, give me heists", "heists", "", "zombies")]
    [InlineData("done with superheroes, westerns please", "westerns", "", "superheroes")]
    [InlineData("I'm so done with zombies. heist films", "heist", "", "zombies")]
    [InlineData("sick and tired of vampires, westerns", "westerns", "", "vampires")]
    [InlineData("no, westerns", "westerns", "", "")]
    public void A_negated_word_is_lifted_into_the_negations_and_never_read_as_a_topic(
        string message, string concepts, string genres, string negations)
    {
        static string[] List(string text) => text.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var failed = PlanResult.Failed(new PlanFailure(AgentFailureKind.MalformedAnswer, null, false), LlmCallMeta.None);
        foreach (var plan in new[]
                 {
                     FallbackPlans.CarriedForward(failed, message, ConversationState.Empty, Now),
                     FallbackPlans.Keyless(message, ConversationState.Empty, Now),
                 })
        {
            Assert.Equal(List(concepts), plan.Delta.Concepts);
            Assert.Equal(List(genres), plan.Delta.Genres);
            Assert.Equal(List(negations), plan.Delta.Negations);

            // Through the reducer to what the judge is told: nothing ruled out is ever "Requires".
            var state = ConversationStateReducer.Apply(ConversationState.Empty, plan, MediaScope.Any);
            var request = AskReelEngine.JudgeRequest(message, state);
            var requires = (request.Split(". ").FirstOrDefault(p => p.StartsWith("Requires: ", StringComparison.Ordinal)) ?? "Requires: ")
                ["Requires: ".Length..].Split(", ", StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal([.. List(concepts), .. List(genres)], state.Hard.Concepts.Select(c => c.UserWords));
            foreach (var negated in plan.Delta.Negations)
            {
                // The phrase ruled out is never required, and a word ruled out on its own is in no requirement.
                bool Names(string required) =>
                    NegationTerms.EqualsAnyForm(required, negated) || NegationTerms.EqualsAnyForm(negated, required)
                    || (!negated.Contains(' ', StringComparison.Ordinal)
                        && required.Split(' ').Any(w => NegationTerms.EqualsAnyForm(w, negated) || NegationTerms.EqualsAnyForm(negated, w)));
                Assert.DoesNotContain(state.Hard.Concepts, c => Names(c.UserWords));
                Assert.DoesNotContain(requires, Names);
            }
        }
    }

    /// <summary>
    /// HREV6-ASK-01-R1: the media word of a ruled-out topic is that topic's noun, not the medium. With
    /// no model, "superhero movies? no thanks, give me westerns" and "no superhero movies, westerns"
    /// ruled out "movies" and searched only shows. A medium is ruled out when it is what the user
    /// refused ("samurai, no tv shows", "tv shows? nah, something with samurai"), and asked for as ever.
    /// </summary>
    [Theory]
    [InlineData("superhero movies? no thanks, give me westerns", null)]
    [InlineData("superhero movies? no, give me westerns", null)]
    [InlineData("no superhero movies, westerns", null)]
    [InlineData("I'm sick of superhero movies, give me westerns", null)]
    [InlineData("vampire tv shows? nah", null)]
    [InlineData("superhero movies? no thanks, westerns, no tv shows", MediaScope.Movies)]
    [InlineData("samurai, no tv shows", MediaScope.Movies)]
    [InlineData("tv shows? nah, something with samurai", MediaScope.Movies)]
    [InlineData("no movies, samurai", MediaScope.Shows)]
    [InlineData("no superhero movies, westerns films", MediaScope.Movies)]
    public void The_noun_of_a_ruled_out_topic_never_rules_out_its_medium(string message, MediaScope? expected)
    {
        Assert.Equal(expected, PlanSanitizer.UserMedia(null, message));
        Assert.Equal(expected, FallbackPlans.Keyless(message, ConversationState.Empty, Now).Delta.Media);
    }

    /// <summary>
    /// HREV6-ASK-01, through the engine's own chains: the keyless plan is cleaned by the sanitiser
    /// (which dropped "spy thrillers excluding James Bond" whole as an echo, leaving a broad search with
    /// no negation), and the carried-forward plan has its pointer resolved — then both are reduced into
    /// the state the judge reads. On a first turn and on a follow-up, no word the user ruled out, and no
    /// cue word, is ever a hard requirement or on the judge's "Requires" line, and each is ruled out.
    /// </summary>
    [Theory]
    [InlineData("heist movies excluding anything with guns", "guns")]
    [InlineData("horror movies apart from slashers", "slashers")]
    [InlineData("superhero movies? no thanks, give me westerns", "superhero")]
    [InlineData("I'm sick of superheroes, give me westerns", "superheroes")]
    [InlineData("action movies sans superheroes", "superheroes")]
    [InlineData("spy thrillers excluding James Bond", "james bond")]
    [InlineData("tired of vampires, give me heists", "vampires")]
    [InlineData("superhero movies? no, give me westerns", "superhero")]
    [InlineData("superhero movies? no. westerns", "superhero")]
    [InlineData("superhero movies, not for me. westerns", "superhero")]
    [InlineData("sick to death of superheroes, give me westerns", "superheroes")]
    [InlineData("I've had it with zombies, give me heists", "zombies")]
    [InlineData("done with superheroes, westerns please", "superheroes")]
    public void A_ruled_out_topic_never_reaches_the_requirements_on_either_fallback_chain(string message, string negated)
    {
        string[] cueWords = ["excluding", "apart", "from", "sans", "sick", "tired", "thanks", "no", "of", "death", "done", "had", "with", "for", "me"];
        var failed = PlanResult.Failed(new PlanFailure(AgentFailureKind.TimedOut, null, false), LlmCallMeta.None);
        var later = ConversationState.Empty with
        {
            Version = 2,
            Hard = HardConstraints.None with { Concepts = [new ConceptTerm("crime", [], [])] },
        };
        foreach (var start in new[] { ConversationState.Empty, later })
        {
            var keyless = PlanSanitizer.Clean(FallbackPlans.Keyless(message, start, Now), message, start, FilterRuleSet.FromRows([]));
            var carried = PlanSanitizer.PointFallback(FallbackPlans.CarriedForward(failed, message, start, Now), message, start);
            foreach (var plan in new[] { keyless, carried })
            {
                var state = ConversationStateReducer.Apply(start, plan, MediaScope.Any);
                var requires = AskReelEngine.JudgeRequest(message, state).Split(". ")
                    .Where(p => p.StartsWith("Requires: ", StringComparison.Ordinal))
                    .SelectMany(p => p["Requires: ".Length..].Split(", "))
                    .ToList();
                bool Names(string required) => required.Split(' ').Any(w =>
                    negated.Split(' ').Any(n => NegationTerms.EqualsAnyForm(w, n) || NegationTerms.EqualsAnyForm(n, w))
                    || cueWords.Contains(w));

                Assert.Contains(state.UserNegations, n => NegationTerms.EqualsAnyForm(n, negated) || NegationTerms.EqualsAnyForm(negated, n));
                Assert.DoesNotContain(state.Hard.Concepts, c => Names(c.UserWords));
                Assert.DoesNotContain(state.Soft.Moods, Names);
                Assert.DoesNotContain(requires, Names);
                // The user's request still stands: the turn is never a broad unchecked search, and a
                // topic's noun ("superhero movies") never rules its medium out.
                Assert.NotEmpty(state.Hard.Concepts);
                Assert.NotEqual(MediaScope.Shows, state.Hard.Media);
            }
        }
    }

    /// <summary>
    /// HREV5-ASK-01: "what is Better Call Saul about?" searched "9 unseen call saul titles" and "what can
    /// you do?" showed sixteen random unchecked titles. A question about a named title is an item
    /// question about that title, small talk about Ask Reel is small talk, and neither searches; a
    /// search that merely contains the words is still a search.
    /// </summary>
    [Theory]
    [InlineData("what is Better Call Saul about?", "ItemQuestion", "better call saul")]
    [InlineData("What's Heat about", "ItemQuestion", "heat")]
    [InlineData("what was The Wire about?", "ItemQuestion", "the wire")]
    [InlineData("what is that about?", "ItemQuestion", null)]
    [InlineData("what can you do?", "Chitchat", null)]
    [InlineData("hey Reel, what can you do?", "Chitchat", null)]
    [InlineData("how does this work", "Chitchat", null)]
    [InlineData("thanks!", "Chitchat", null)]
    [InlineData("what can you do with zombies", "NewRequest", null)]
    [InlineData("what is a good movie about revenge", "NewRequest", null)]
    [InlineData("hey, heist movies", "NewRequest", null)]
    // Round 7: the class, not the cited phrasings — a question about a named title in any of its
    // common shapes, and small talk however it is put; a request in the same shape is still a request.
    [InlineData("tell me about Better Call Saul", "ItemQuestion", "better call saul")]
    [InlineData("tell me more about The Wire please", "ItemQuestion", "the wire")]
    [InlineData("is Better Call Saul any good?", "ItemQuestion", "better call saul")]
    [InlineData("is Heat worth watching?", "ItemQuestion", "heat")]
    [InlineData("what's the plot of Better Call Saul?", "ItemQuestion", "better call saul")]
    [InlineData("what happens in Dune?", "ItemQuestion", "dune")]
    [InlineData("who's in Heat?", "ItemQuestion", "heat")]
    [InlineData("is it any good?", "ItemQuestion", null)]
    [InlineData("what can you help me with?", "Chitchat", null)]
    [InlineData("what are you capable of?", "Chitchat", null)]
    [InlineData("what kind of things can I ask?", "Chitchat", null)]
    [InlineData("what can you do for me?", "Chitchat", null)]
    [InlineData("can you help me?", "Chitchat", null)]
    [InlineData("tell me about yourself", "Chitchat", null)]
    [InlineData("tell me about some heist movies", "NewRequest", null)]
    [InlineData("tell me about heist films from the 90s", "NewRequest", null)]
    [InlineData("is there anything good tonight?", "NewRequest", null)]
    [InlineData("is horror any good", "NewRequest", null)]
    [InlineData("can you help me find a heist movie", "NewRequest", null)]
    [InlineData("what do you recommend with zombies?", "NewRequest", null)]
    public void A_question_about_a_title_or_about_ask_reel_is_never_searched(string message, string act, string? name)
    {
        var failed = PlanResult.Failed(new PlanFailure(AgentFailureKind.Truncated, null, false), LlmCallMeta.None);
        foreach (var plan in new[]
                 {
                     FallbackPlans.CarriedForward(failed, message, ConversationState.Empty, Now),
                     FallbackPlans.Keyless(message, ConversationState.Empty, Now),
                 })
        {
            Assert.Equal(act, plan.Act.ToString());
            if (act == "ItemQuestion")
            {
                Assert.Equal(name, Assert.Single(plan.Tools, t => t.Tool == AskTool.TitleFacts).Name);
            }

            if (act != "NewRequest")
            {
                Assert.Empty(plan.Delta.Concepts);
                Assert.Empty(plan.Delta.Genres);
            }
        }
    }

    /// <summary>
    /// HREV5-ASK-02's model-less half: a genre written by a word the conflict detector reads ("sitcoms",
    /// "docuseries", "chick flicks") is that genre, so the keyless request keeps it — and with it the
    /// conflict frame — instead of searching an unknown topic.
    /// </summary>
    [Theory]
    [InlineData("a good docuseries", "documentary")]
    [InlineData("chick flicks", "romance")]
    [InlineData("nature docs", "documentary")]
    public void A_genre_by_another_word_is_read_as_the_genre_with_no_model(string message, string genre)
    {
        var plan = FallbackPlans.Keyless(message, ConversationState.Empty, Now);

        Assert.Contains(genre, plan.Delta.Genres);
        Assert.DoesNotContain(plan.Delta.Concepts, c => c.Contains(message.Split(' ')[^1], StringComparison.Ordinal));
    }

    /// <summary>
    /// "Sitcoms" is now a genre word of the concept vocabulary itself (a sitcom is a comedy series), so
    /// the keyless reading keeps it as a topic and the topic READS as the comedy genre — the same
    /// requirement the conflict detector names, now counted over the comedy shelf too.
    /// </summary>
    [Fact]
    public void Sitcoms_is_a_topic_that_reads_as_the_comedy_genre_with_no_model()
    {
        var plan = FallbackPlans.Keyless("sitcoms", ConversationState.Empty, Now);
        var concepts = plan.Delta.Concepts.Select(c => new ConceptTerm(c, [], [])).Concat(plan.Delta.Genres.Select(g => new ConceptTerm(g, [], [g])));

        Assert.Equal(["comedy"], HardConstraintFilter.Read([.. concepts]).SelectMany(HardConstraintFilter.GenresOf).Distinct());
    }

    /// <summary>The credit a model-less person request means (HREV5-RET-02): directing or acting only when its words say so.</summary>
    [Theory]
    [InlineData("movies with Liam Neeson", PersonRole.Any)]
    [InlineData("liam neeson films", PersonRole.Any)]
    [InlineData("films directed by Christopher Nolan", PersonRole.Director)]
    [InlineData("christopher nolan as director", PersonRole.Director)]
    [InlineData("anything starring Zendaya", PersonRole.Cast)]
    public void A_model_less_person_request_reads_its_role_from_the_words(string message, PersonRole role) =>
        Assert.Equal(role, AskReelEngine.FallbackRole(message));

    /// <summary>
    /// HREV5-RET-02 (round 7): the reader handed the grounder role words and broken names. "movies
    /// starring Liam Neeson" was the topic "starring liam neeson" and "films directed by Christopher
    /// Nolan" kept "directed" as a hard topic; "Will Smith" was "smith", "Al Pacino" "pacino", "Robert
    /// De Niro" the two topics "robert" and "niro", "Samuel L. Jackson" "samuel" and "jackson", and
    /// "Robert Downey Jr." "robert downey". A role word is the role, and a capitalised name is read
    /// whole. Varied over where the name stands (first, after "with", before a genre), over a negated
    /// name, over a title after "like", and over messages whose capitals mark no name.
    /// </summary>
    [Theory]
    [InlineData("movies starring Liam Neeson", "liam neeson", "", "")]
    [InlineData("films directed by Christopher Nolan", "christopher nolan", "", "")]
    [InlineData("Will Smith movies", "will smith", "", "")]
    [InlineData("comedies with Will Ferrell", "comedies|will ferrell", "", "")]
    [InlineData("something with Al Pacino", "al pacino", "", "")]
    [InlineData("Ed Harris films", "ed harris", "", "")]
    [InlineData("movies with Robert De Niro", "robert de niro", "", "")]
    [InlineData("movies with Robert de Niro", "robert de niro", "", "")]
    [InlineData("Samuel L. Jackson movies", "samuel l jackson", "", "")]
    [InlineData("anything with Robert Downey Jr. please", "robert downey jr", "", "")]
    [InlineData("Jean-Claude Van Damme action movies", "jean claude van damme", "action", "")]
    [InlineData("action movies, nothing with Will Smith", "action", "", "will smith")]
    // Capitals that mark no name leave the reading as it was.
    [InlineData("Tense Heist Movies", "heist", "", "")]
    [InlineData("TENSE HEIST MOVIES", "heist", "", "")]
    [InlineData("Movies From The 80s", "", "", "")]
    [InlineData("Any Tom Hanks films?", "tom hanks", "", "")]
    [InlineData("Show me Will Smith movies", "will smith", "", "")]
    public void A_role_word_is_the_role_and_a_capitalised_name_is_read_whole(
        string message, string concepts, string genres, string negations)
    {
        static string[] List(string text) => text.Split('|', StringSplitOptions.RemoveEmptyEntries);
        var failed = PlanResult.Failed(new PlanFailure(AgentFailureKind.Truncated, null, false), LlmCallMeta.None);
        foreach (var plan in new[]
                 {
                     FallbackPlans.CarriedForward(failed, message, ConversationState.Empty, Now),
                     FallbackPlans.Keyless(message, ConversationState.Empty, Now),
                 })
        {
            Assert.Equal(List(concepts), plan.Delta.Concepts);
            Assert.Equal(List(genres), plan.Delta.Genres);
            Assert.Equal(List(negations), plan.Delta.Negations);
            Assert.DoesNotContain(plan.Delta.Concepts, c => c.Split(' ').Any(FallbackReader.IsRoleWord));
        }
    }

    /// <summary>A capitalised title after "like" is still the reference, whole, and never also a topic.</summary>
    [Theory]
    [InlineData("movies like The Dark Knight", "the dark knight")]
    [InlineData("shows like Better Call Saul but funnier", "better call saul")]
    public void A_capitalised_title_after_like_is_the_reference(string message, string reference)
    {
        var plan = FallbackPlans.Keyless(message, ConversationState.Empty, Now);

        Assert.Equal([reference], plan.Delta.ReferenceNames);
        Assert.DoesNotContain(plan.Delta.Concepts, c => reference.Split(' ').Any(w => w.Length > 3 && c.Contains(w, StringComparison.Ordinal)));
    }

    /// <summary>
    /// HREV5-RET-02 (round 7): the grounder looked names up by an exact pattern, which no name with a
    /// full stop in it could match. It now finds the stored name however it is punctuated and keeps
    /// only the one whose words are exactly the user's, or those and a suffix the user left off.
    /// </summary>
    [Theory]
    [InlineData("Samuel L. Jackson", "samuel l jackson", true)]
    [InlineData("Robert Downey Jr.", "robert downey jr", true)]
    [InlineData("Robert Downey Jr.", "robert downey", true)]
    [InlineData("Robert De Niro", "robert de niro", true)]
    [InlineData("Jean-Claude Van Damme", "jean claude van damme", true)]
    [InlineData("Will Smith", "will smith", true)]
    [InlineData("Will Smithers", "will smith", false)]
    [InlineData("William Smith", "will smith", false)]
    [InlineData("Samuel Jackson", "samuel l jackson", false)]
    [InlineData("Robert Downey Sr. III", "robert downey", false)]
    [InlineData("Zendaya", "zendaya", true)]
    public void A_person_name_matches_its_words_whatever_the_punctuation(string stored, string typed, bool matches)
    {
        var words = typed.Split(' ');

        Assert.Equal(matches, AskReelEngine.PersonNameMatches(stored, words));
        // The database pattern never excludes a name the words match.
        if (matches)
        {
            var pattern = "^" + string.Join(".*", AskReelEngine.PersonNamePattern(words).Split('%').Select(System.Text.RegularExpressions.Regex.Escape)) + "$";
            Assert.Matches(new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase), stored);
        }
    }
}
