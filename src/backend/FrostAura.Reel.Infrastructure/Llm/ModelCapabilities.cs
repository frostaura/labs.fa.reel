using System.Collections.Concurrent;
using FrostAura.Reel.Application.Llm;
using Microsoft.Extensions.Configuration;

namespace FrostAura.Reel.Infrastructure.Llm;

/// <summary>
/// What each model has been seen to accept: strict <c>json_schema</c> output, and which reasoning
/// efforts. Learned from the provider's own rejections and remembered for the process, so a
/// rejected request is paid for (in latency) once per model, not once per call.
///
/// <para><b>Per model, not per adapter.</b> Each adapter used to carry its own
/// <c>_strictSchemaSupported</c> flag — four copies of one fact, and the search agent's was
/// process-wide while the others were per instance, so the same model could be probed four
/// different ways. Registered as a DI singleton; an adapter built by hand (a test) gets its own
/// table, so one test's rejection never leaks into the next.</para>
///
/// <para><b>Seeded from configuration</b> as well as learned: <c>LLM_EFFORT_UNSUPPORTED</c> lists
/// <c>model=effort</c> pairs a model is known to reject (comma-separated), for a model that rejects
/// <c>none</c> on every call and should not pay the first rejection after each restart.</para>
/// </summary>
public sealed class ModelCapabilities
{
    public const string EffortUnsupportedSetting = "LLM_EFFORT_UNSUPPORTED";

    private readonly ConcurrentDictionary<string, bool> _strictUnsupported = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Model, string Effort), bool> _effortUnsupported = new();

    public ModelCapabilities()
    {
    }

    public ModelCapabilities(IConfiguration configuration)
    {
        if (configuration[EffortUnsupportedSetting] is not { Length: > 0 } seeded)
        {
            return;
        }

        foreach (var pair in seeded.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = pair.LastIndexOf('=');
            if (at > 0 && at < pair.Length - 1)
            {
                _effortUnsupported[(pair[..at], pair[(at + 1)..].ToLowerInvariant())] = true;
            }
        }
    }

    public bool SupportsStrictSchema(string model) => !_strictUnsupported.ContainsKey(model);

    public void MarkStrictSchemaUnsupported(string model) => _strictUnsupported[model] = true;

    /// <summary>
    /// The effort actually sent for <paramref name="requested"/> on <paramref name="model"/>:
    /// the requested value, or its fallback chain (none → low → no parameter) past every value the
    /// model has rejected. Null means "send no reasoning parameter". Part of every cache key a
    /// result feeds, through <see cref="CacheSalt"/>.
    /// </summary>
    public string? EffectiveEffort(string model, string? requested)
    {
        var effort = requested;
        while (effort is not null && _effortUnsupported.ContainsKey((model, effort)))
        {
            effort = ReasoningEfforts.FallbackFor(effort);
        }

        return effort;
    }

    public void MarkEffortUnsupported(string model, string effort) => _effortUnsupported[(model, effort)] = true;

    /// <summary>The model-and-effort part of a cache key: <c>model|effort</c>, effort <c>-</c> when none is sent.</summary>
    public string CacheSalt(string model, string? requestedEffort) =>
        string.Concat(model, "|", EffectiveEffort(model, requestedEffort) ?? "-");
}
