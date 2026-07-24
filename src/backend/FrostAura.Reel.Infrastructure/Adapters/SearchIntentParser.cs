using System.Text.Json;
using FrostAura.Reel.Domain.Catalog;
using FrostAura.Reel.Domain.Ports;
using FrostAura.Reel.Domain.Ports.Tmdb;

namespace FrostAura.Reel.Infrastructure.Adapters;

/// <summary>
/// Parses an LLM discovery-spec JSON object into a <see cref="SearchIntent"/> — shared by the
/// query interpreter and the conversational agent so the contract (allowed genre vocabulary,
/// media-type mapping, keyword fallback) is single-sourced.
/// </summary>
internal static class SearchIntentParser
{
    public static SearchIntent FromJson(JsonElement root, string fallbackText, SearchIntent heuristicFallback)
    {
        string[] StrArray(string name) =>
            root.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.Array
                ? el.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!.Trim().ToLowerInvariant()).Where(s => s.Length > 0).Distinct().Take(8).ToArray()
                : [];

        var genres = StrArray("genres").Where(TmdbGenres.SlugToIds.ContainsKey).ToArray();
        var keywords = StrArray("keywords");
        var media = StrArray("mediaTypes")
            .Select(m => m.StartsWith("movie") ? MediaType.Movie : m is "tv" or "show" or "series" ? MediaType.Show : (MediaType?)null)
            .Where(m => m is not null).Select(m => m!.Value).Distinct().ToArray();
        int? minYear = root.TryGetProperty("minYear", out var y) && y.ValueKind == JsonValueKind.Number && y.TryGetInt32(out var yr) && yr > 1870
            ? yr : null;
        var freeText = root.TryGetProperty("freeText", out var f) && f.ValueKind == JsonValueKind.String && f.GetString() is { Length: > 0 } s
            ? s : fallbackText;
        var people = ParsePeople(root);

        // A model that returned nothing usable still gets the heuristic's keywords so discovery
        // never fires empty — but a named person IS the discovery signal, so it shortcuts that.
        if (genres.Length == 0 && keywords.Length == 0 && people.Count == 0)
        {
            return heuristicFallback with
            {
                MediaTypes = media.Length > 0 ? media : heuristicFallback.MediaTypes,
                MinYear = minYear ?? heuristicFallback.MinYear,
            };
        }

        return new SearchIntent(genres, keywords, media, freeText, minYear, people);
    }

    /// <summary>Parses a "people":[{"name","role"}] array — role ∈ director|cast|any (default any).</summary>
    private static IReadOnlyList<PersonQuery> ParsePeople(JsonElement root)
    {
        if (!root.TryGetProperty("people", out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<PersonQuery>();
        foreach (var el in arr.EnumerateArray().Take(3))
        {
            var name = el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : el.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var roleStr = el.ValueKind == JsonValueKind.Object && el.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()?.Trim().ToLowerInvariant() : null;
            var role = roleStr switch
            {
                "director" or "directed" or "directing" => PersonRole.Director,
                "cast" or "actor" or "actress" or "star" or "starring" => PersonRole.Cast,
                _ => PersonRole.Any,
            };
            result.Add(new PersonQuery(name.Trim(), role));
        }

        return result;
    }
}
