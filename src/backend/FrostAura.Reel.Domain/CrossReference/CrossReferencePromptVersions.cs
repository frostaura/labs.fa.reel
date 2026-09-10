namespace FrostAura.Reel.Domain.CrossReference;

/// <summary>
/// The prompt generations that exist, as a closed set.
///
/// <para><b>In the domain rather than on the adapter</b> because the version is a persisted key
/// part — every <c>TitleCrossReferenceVerdict</c> and every <c>CrossReferenceEvaluation</c> row
/// carries one — and because the endpoint that lets the founder select a generation has to be able
/// to reject an unknown one without the API layer reaching into an OpenRouter adapter. A run
/// recorded under version 7 would be rows nothing can render and nothing will ever judge against
/// again.</para>
///
/// <para><b>Adding a generation is three edits, not one:</b> a constant here, the prompt and its
/// reason vocabulary in the adapter, and the evidence mapping in
/// <c>NeighbourFinder.ForPromptVersion</c>. What a stored verdict MEANS is the union of those
/// three, which is exactly why the version is in the key.</para>
/// </summary>
public static class CrossReferencePromptVersions
{
    /// <summary>The first generation, and the one the serving job rides.</summary>
    public const int First = 1;

    /// <summary>
    /// The second — neighbour-led candidate lines, five neighbours, an explicit genre affinity
    /// line, base-rate-anchored confidence and a <c>neighbour-mixed</c> reason. An experiment until
    /// it is measured against the first; selectable only from the founder-only backtest endpoint.
    /// </summary>
    public const int Second = 2;

    /// <summary>Every generation, ascending.</summary>
    public static readonly int[] All = [First, Second];

    /// <summary>Whether a caller-supplied version names a generation that exists.</summary>
    public static bool IsKnown(int promptVersion) => Array.IndexOf(All, promptVersion) >= 0;
}
