using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrostAura.Reel.Application.Search.AskReel.Engine;

/// <summary>
/// <c>SearchConversations.StateJson</c>: the state, every enum as its name. A row this image cannot
/// read — written by a newer image with a value this one does not know, or simply corrupt — reads
/// as null, and the engine rebuilds the state from the turn record instead (§5.0 rule 8). The turn
/// record is the source of truth; this column is a cache of it.
/// </summary>
public static class ConversationStateJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The column value for <paramref name="state"/>.</summary>
    public static string Write(ConversationState state) => JsonSerializer.Serialize(state, Options);

    /// <summary>The state a column holds, or null when it holds none this image can read.</summary>
    public static ConversationState? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var state = JsonSerializer.Deserialize<ConversationState>(json, Options);
            return state is { Hard: not null, Soft: not null } ? Normalise(state) : null;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Nulls a reader of an older row would otherwise trip on, as empty lists.</summary>
    private static ConversationState Normalise(ConversationState state) => state with
    {
        Hard = state.Hard with
        {
            Concepts = state.Hard.Concepts ?? [],
            People = state.Hard.People ?? [],
        },
        Soft = state.Soft with
        {
            Moods = state.Soft.Moods ?? [],
            Directions = state.Soft.Directions ?? [],
            ReferenceTitleIds = state.Soft.ReferenceTitleIds ?? [],
        },
        UserNegations = state.UserNegations ?? [],
        Rejected = state.Rejected ?? [],
        Liked = state.Liked ?? [],
        LastPresented = state.LastPresented ?? [],
    };
}
