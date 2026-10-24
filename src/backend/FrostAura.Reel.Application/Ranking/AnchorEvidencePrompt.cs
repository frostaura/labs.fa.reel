using System.Globalization;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Application.Ranking;

/// <summary>
/// Hands the anchored-row gate's evidence to the re-rank model, so a why-line on an anchored card
/// names the link the gate actually checked instead of inventing one.
///
/// <para><b>Why.</b> The gate admitted every card on a "Because you loved X" rail for a concrete
/// reason (a shared person, keyword or theme), but the model was never told which: on the founder's
/// snapshot all seven model lines on the one dealt rail spoke of "the anchor's" mood, and the line
/// on Doctor Who — admitted to a Sherlock rail on a shared theme — described a link of its own. The
/// re-rank input already carries a "picked for" list for each candidate (its strongest reasons), so
/// the evidence leads that list in words the model can quote, with the anchor named. No prompt or
/// port change: the input shape is WS-1's, and this only fills a field it already has.</para>
/// </summary>
public static class AnchorEvidencePrompt
{
    private static readonly RerankTitleContext Empty = new(
        Tone: null, Era: null, Themes: [], Darkness: null, Pacing: null, Complexity: null,
        EmotionalIntensity: null, Humor: null, Optimism: null, EnsembleVsSolo: null,
        Director: null, Writer: null, TopCast: [], TopContributions: []);

    /// <summary>
    /// The candidate's re-rank context with the evidence leading its "picked for" list, or the
    /// context unchanged when there is no evidence (every non-anchored row).
    /// </summary>
    public static RerankTitleContext? With(
        RerankTitleContext? context, IReadOnlyList<SharedEvidence>? evidence, string? anchorName)
    {
        if (evidence is not { Count: > 0 } || string.IsNullOrWhiteSpace(anchorName))
        {
            return context;
        }

        var reasons = Describe(evidence, anchorName);
        var baseContext = context ?? Empty;
        return baseContext with { TopContributions = [.. reasons, .. baseContext.TopContributions] };
    }

    /// <summary>
    /// A context carrying only the evidence, for a caller with no enrichment of its own (the
    /// paginator); null when there is no evidence, which is what that caller sent before.
    /// </summary>
    public static RerankTitleContext? ContextFor(IReadOnlyList<SharedEvidence>? evidence, string? anchorName) =>
        With(null, evidence, anchorName);

    /// <summary>The evidence as short clauses naming the anchor — "same director as Heat: Michael Mann".</summary>
    public static IReadOnlyList<string> Describe(IReadOnlyList<SharedEvidence> evidence, string anchorName) =>
        [.. evidence.Select(e => (e.Kind, e.Role) switch
        {
            ("person", "director") => string.Create(CultureInfo.InvariantCulture, $"same director as {anchorName}: {e.Label}"),
            ("person", "writer") => string.Create(CultureInfo.InvariantCulture, $"same writer as {anchorName}: {e.Label}"),
            ("person", _) => string.Create(CultureInfo.InvariantCulture, $"{e.Label} also stars in {anchorName}"),
            ("keyword", _) => string.Create(CultureInfo.InvariantCulture, $"shares the {e.Label} premise with {anchorName}"),
            _ => string.Create(CultureInfo.InvariantCulture, $"shares {anchorName}'s theme of {e.Label}"),
        })];
}
