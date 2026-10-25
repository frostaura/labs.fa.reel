using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Domain.Catalog;

/// <summary>Why one title's extraction produced no usable answer.</summary>
public enum AttributeFailureClass
{
    /// <summary>The extraction succeeded.</summary>
    None = 0,

    /// <summary>
    /// The extractor said only "no answer" — it does not implement
    /// <see cref="IClassifyingAttributeExtractor"/>. Charged like <see cref="Parse"/>, because the
    /// attempt budget is the only bound on spend for a failure nobody can explain; counted
    /// separately in the run log so the gap stays visible.
    /// </summary>
    Unclassified,

    /// <summary>
    /// The request never produced a completion: a dropped connection, a body read that died after
    /// the headers, a timeout, an HTTP error status. Elapsed time is the useful variable, so it backs
    /// off by time and never spends the attempt budget.
    /// </summary>
    Transport,

    /// <summary>
    /// A completion came back and could not be used — empty, truncated, or missing / mistyping a
    /// required scalar after the extractor's own repair pass. Spends one attempt.
    /// </summary>
    Parse,
}

/// <summary>One title's extraction result, with the failure class when there is no answer.</summary>
public sealed record AttributeExtractionOutcome(ExtractedTitleAttributes? Attributes, AttributeFailureClass Failure)
{
    public static AttributeExtractionOutcome Success(ExtractedTitleAttributes attributes) =>
        new(attributes, AttributeFailureClass.None);

    public static AttributeExtractionOutcome Failed(AttributeFailureClass failure) => new(null, failure);
}

/// <summary>
/// An attribute extractor that can say WHY a title failed. The enrich handler uses it whenever the
/// configured <see cref="ITitleAttributeExtractor"/> also implements it; without it every null is
/// <see cref="AttributeFailureClass.Unclassified"/>.
/// <para>
/// It exists because the plain port collapses a dropped connection and a malformed answer into the
/// same null, and the handler charged both against a two-attempt budget: on the founder's catalogue
/// 2,038 titles were left Failed — 1,110 of the 1,116 extractor warnings in 72 hours were network
/// drops, and none were parse failures (E-6).
/// </para>
/// </summary>
public interface IClassifyingAttributeExtractor
{
    /// <summary>
    /// Row-aligned with <paramref name="inputs"/>, exactly like
    /// <see cref="ITitleAttributeExtractor.ExtractAsync"/>, with the same billing behaviour.
    /// </summary>
    Task<IReadOnlyList<AttributeExtractionOutcome>> ExtractClassifiedAsync(
        IReadOnlyList<TitleAttributeInput> inputs, CancellationToken ct = default);
}

/// <summary>
/// The one exception an attribute extractor may throw for a WHOLE-BATCH transport failure — the
/// request never produced a completion. The enrich handler books it, together with
/// <see cref="System.Net.Http.HttpRequestException"/>, <see cref="System.IO.IOException"/>,
/// <see cref="TimeoutException"/> and an HTTP timeout's cancellation, as
/// <see cref="AttributeFailureClass.Transport"/>. Anything else an extractor throws is a defect,
/// not a network fact: it propagates and fails the job, where it is visible, instead of being
/// backed off forever under a transport label.
/// </summary>
public sealed class AttributeExtractionTransportException(string message, Exception? inner = null)
    : Exception(message, inner);
