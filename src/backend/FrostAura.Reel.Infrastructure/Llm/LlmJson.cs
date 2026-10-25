using System.Text;
using System.Text.Json;

namespace FrostAura.Reel.Infrastructure.Llm;

/// <summary>What had to be cut away to read a model's answer as one JSON object.</summary>
[Flags]
public enum LlmJsonSlip
{
    /// <summary>The content was one JSON object and nothing else.</summary>
    None = 0,

    /// <summary>Prose (or anything that is not JSON) came before the object.</summary>
    Preamble = 1,

    /// <summary>Something followed the complete object — a second object, a sign-off, a stray quote.</summary>
    Trailing = 2,
}

/// <summary>
/// Reads the first complete JSON object out of a model's answer.
///
/// <para><b>Why not <c>JsonDocument.Parse</c>.</b> Two answers on the eval stack were thrown away
/// whole for text around a perfectly good object. The interpret call answered "I…" before its JSON
/// under <c>json_object</c> (both attempts, so the turn degraded), and a re-rank retry returned one
/// complete <c>results</c> object followed by the start of a second, so six titles lost their
/// verdicts to "invalid after a single JSON value". <c>Parse</c> demands the whole string be one
/// value; the model's contract is only that the object is in there.</para>
///
/// <para><b>What it will not do.</b> It never repairs: a truncated or malformed object is still no
/// object. It reads the FIRST complete object only — a second one is a model running on, never a
/// correction — and it reports what it cut so the caller can log the slip, because a salvaged
/// answer is still a format failure the per-purpose fallback figures must be able to see.</para>
/// </summary>
public static class LlmJson
{
    /// <summary>How many opening braces are tried before giving up — a preamble may contain one.</summary>
    private const int MaxStarts = 3;

    /// <summary>
    /// The first complete JSON object in <paramref name="content"/>, or null when there is none.
    /// <paramref name="slip"/> says what surrounded it. The caller owns the returned document.
    /// </summary>
    public static JsonDocument? FirstObject(string? content, out LlmJsonSlip slip)
    {
        slip = LlmJsonSlip.None;
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var from = 0;
        for (var attempt = 0; attempt < MaxStarts; attempt++)
        {
            var start = content.IndexOf('{', from);
            if (start < 0)
            {
                return null;
            }

            if (TryRead(content, start, out var doc, out var trailing))
            {
                slip = (string.IsNullOrWhiteSpace(content[..start]) ? LlmJsonSlip.None : LlmJsonSlip.Preamble)
                    | (trailing ? LlmJsonSlip.Trailing : LlmJsonSlip.None);
                return doc;
            }

            from = start + 1;
        }

        return null;
    }

    private static bool TryRead(string content, int start, out JsonDocument? doc, out bool trailing)
    {
        doc = null;
        trailing = false;
        var bytes = Encoding.UTF8.GetBytes(content[start..]);
        var reader = new Utf8JsonReader(bytes, isFinalBlock: true, state: default);
        try
        {
            if (!JsonDocument.TryParseValue(ref reader, out doc) || doc is null)
            {
                return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            doc = null;
            return false;
        }

        var rest = bytes.AsSpan((int)reader.BytesConsumed);
        foreach (var b in rest)
        {
            if (b is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                trailing = true;
                break;
            }
        }

        return true;
    }
}
