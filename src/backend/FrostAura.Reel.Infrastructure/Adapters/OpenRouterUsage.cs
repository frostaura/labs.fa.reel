using System.Text.Json.Serialization;

namespace FrostAura.Reel.Infrastructure.Adapters;

/// <summary>
/// The <c>usage</c> block every OpenAI-compatible response carries — chat completions and
/// embeddings alike. One shape shared by all four adapters so the ledger cannot end up with two
/// different ideas of what a token is.
///
/// Explicit <see cref="JsonPropertyNameAttribute"/>s rather than a naming policy: the adapters
/// disagree on their serializer options (Web vs snake_case), and the wire names are fixed.
/// Every field is nullable — a provider that omits usage must leave the ledger saying "unknown",
/// never "zero".
/// </summary>
internal sealed record OpenRouterUsage(
    [property: JsonPropertyName("prompt_tokens")] long? PromptTokens,
    [property: JsonPropertyName("completion_tokens")] long? CompletionTokens,
    [property: JsonPropertyName("total_tokens")] long? TotalTokens);
