using FrostAura.Reel.Domain.CrossReference;
using FrostAura.Reel.Domain.Lens;
using FrostAura.Reel.Domain.Ports;

namespace FrostAura.Reel.Tests.Fakes;

/// <summary>
/// A judge that only knows which prompt generation is current, and refuses to judge anything.
///
/// <para>For the callers that need <see cref="ICrossReferenceJudge"/> purely as a source of the
/// current version — <c>AccuracyQuery</c> marks the backtest row that governs what is being served,
/// and nothing else about the port is reachable from there. Throwing rather than returning an empty
/// batch is deliberate: if a test ever DOES reach the judging path through this stub, that is a
/// wiring mistake worth failing on rather than an empty result worth explaining.</para>
/// </summary>
public sealed class FixedVersionCrossReferenceJudge(int promptVersion) : ICrossReferenceJudge
{
    public bool IsAvailable => false;

    public string ModelId => "stub/version-only";

    public int PromptVersion => promptVersion;

    public Task<CrossReferenceBatchResult> JudgeAsync(
        IReadOnlyList<CrossReferenceCandidate> candidates,
        LensPersona persona,
        CrossReferenceRound round,
        string usagePurpose,
        CrossReferenceJudgeOptions options,
        CancellationToken ct = default) =>
        throw new InvalidOperationException(
            "This stub exists to report a prompt version; nothing under test should be judging through it.");
}
