using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Sync;

namespace FrostAura.Reel.Tests.Fakes;

/// <summary>
/// The <see cref="ISearchAgent"/> that is switched OFF — <c>IsAvailable</c> is false and every
/// method throws. It is what a feed-build or paginator test wires when the pipeline under test
/// must be shown to work with no conversational agent at all: an accidental call is then a loud
/// <see cref="NotSupportedException"/> rather than a silently-degraded path that still passes.
///
/// <para>Promoted from eight byte-identical private copies (VisibleRecalibration,
/// FeedPaginatorMediaFilter, BuildFeedRunnerPath, TastePriorFeed, InterleavingIntegration,
/// BuildFeedNeverEmpty, EnrichScheduling and CrossReferenceJob tests). Deliberately minimal: it
/// implements exactly what the port declares and nothing else, so widening
/// <see cref="ISearchAgent"/> is a one-file edit here instead of an eight-file one.</para>
/// </summary>
internal sealed class UnavailableSearchAgent : ISearchAgent
{
    public bool IsAvailable => false;

    public string ModelId => "stub";

    public Task<AgentReply> InterpretAsync(
        IReadOnlyList<ChatTurn> history, string message, string tasteSummary,
        IReadOnlyList<string> shownTitles, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<RerankResult>> RerankBatchAsync(
        IReadOnlyList<RerankInput> titles, string query, LensPersona persona, string? anchorName = null,
        string usagePurpose = ApiUsagePurpose.Rerank, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
