using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrostAura.Reel.Application.Persistence;

/// <summary>
/// The JSON half of program §5.0 rule 8. Enums inside persisted JSON (a jsonb payload, a cursor,
/// a stored intent) are rows too: a newer image may have written a member this one has never heard
/// of, and System.Text.Json's string converter throws on it (failing the whole outbox batch the
/// payload sits in), while its default number handling quietly casts any integer into the enum.
/// Both readers here map anything this build does not define to a declared fallback instead.
///
/// <para>Reads an exact member name, or a number that is a defined member (what a writer with the
/// default options wrote), so it reads every row the existing writers produced. Writes the member
/// name, exactly as <see cref="JsonStringEnumConverter"/> does. Putting it on a writer that used to
/// write numbers changes that writer's format, which an older image without this reader cannot
/// read; switch a writer only once every image in the rollback window reads through this.</para>
/// </summary>
public static class TolerantJsonEnum
{
    /// <summary>
    /// The value <paramref name="reader"/>'s current token reads as, or <paramref name="fallback"/>
    /// for a name this build does not define, a number that is not a member, a differently cased
    /// name, or any other token (null, a boolean, an object, which is skipped).
    /// </summary>
    public static TEnum Read<TEnum>(ref Utf8JsonReader reader, TEnum fallback)
        where TEnum : struct, Enum
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return Named<TEnum>(reader.GetString()) ?? fallback;

            case JsonTokenType.Number:
                return reader.TryGetInt64(out var number) && FromNumber<TEnum>(number) is { } numbered
                    ? numbered
                    : fallback;

            case JsonTokenType.StartObject or JsonTokenType.StartArray:
                reader.Skip();
                return fallback;

            default:
                return fallback;
        }
    }

    /// <summary>
    /// The member whose name is exactly <paramref name="name"/>, or null. Exactly: Enum.TryParse
    /// alone also accepts digits, surrounding spaces, other casing and a comma list ("Movie, Show"
    /// parses as the OR of the two), none of which is a name any build wrote.
    /// </summary>
    public static TEnum? Named<TEnum>(string? name)
        where TEnum : struct, Enum =>
        name is not null
        && Enum.TryParse<TEnum>(name, ignoreCase: false, out var parsed)
        && string.Equals(Enum.GetName(parsed), name, StringComparison.Ordinal)
            ? parsed
            : null;

    private static TEnum? FromNumber<TEnum>(long number)
        where TEnum : struct, Enum
    {
        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) == number)
            {
                return value;
            }
        }

        return null;
    }
}

/// <summary>
/// A JSON-persisted enum that reads anything this build does not define as the schema steward's
/// declared fallback (<see cref="PersistedEnumFallbacks.For{TEnum}"/>). For enums whose fallback is
/// inert in the reading context. Usable as <c>[JsonConverter(typeof(TolerantJsonEnumConverter&lt;T&gt;))]</c>.
/// </summary>
public sealed class TolerantJsonEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override bool HandleNull => true;

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TolerantJsonEnum.Read(ref reader, PersistedEnumFallbacks.For<TEnum>());

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>
/// A JSON-persisted enum that reads anything this build does not define as a value outside the enum
/// (<see cref="PersistedEnumFallbacks.Unrecognised{TEnum}"/>), so every equality test against a member
/// is false and an exhaustive switch takes its default branch. For payloads that drive an external
/// write, where ANY member would be a known-but-wrong instruction: an outbox entry whose media type
/// cannot be read must not be sent to Trakt as a movie.
/// </summary>
public sealed class UnrecognisedJsonEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    public override bool HandleNull => true;

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        TolerantJsonEnum.Read(ref reader, PersistedEnumFallbacks.Unrecognised<TEnum>());

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        // Never written: a value outside the enum would persist as a number no build can name.
        if (!Enum.IsDefined(value))
        {
            throw new JsonException($"{typeof(TEnum).Name} value {value} is not a member and cannot be persisted.");
        }

        writer.WriteStringValue(value.ToString());
    }
}

/// <summary>
/// Applies <see cref="TolerantJsonEnumConverter{TEnum}"/> to every enum a serializer meets, for
/// readers that pass options rather than annotate the type (the type may live in a layer that
/// cannot see this one). A property's own <c>[JsonConverter]</c> still wins over it.
/// </summary>
public sealed class TolerantJsonEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(TolerantJsonEnumConverter<>).MakeGenericType(typeToConvert))!;
}

/// <summary>Serializer options for reading persisted JSON: the defaults, plus tolerant enums.</summary>
public static class PersistedJson
{
    /// <summary>
    /// Read with these wherever persisted JSON is deserialized. Identical to the default options in
    /// every other respect, so adopting them changes nothing but how an enum this build cannot name
    /// is read.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new TolerantJsonEnumConverterFactory());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
