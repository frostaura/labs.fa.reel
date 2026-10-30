namespace FrostAura.Reel.Application.Search.Retrieval.Concepts;

/// <summary>
/// Words that name a title's ORIGINAL LANGUAGE ("korean thrillers", "french films", "a japanese horror
/// movie"), with the ISO 639-1 code TMDB stores in <c>Titles.Language</c>.
///
/// <para><b>Why a language and not a keyword.</b> Read as a keyword stem, "korean" reached tags such as
/// "korean war" and "south korea": the founder's "korean thrillers" counted 38 titles where the
/// catalogue holds 1,008 Korean-language ones, and the reply offered "turning off English only would
/// unlock 25" where the honest number is 407. Worse, no conflict was named at all, although his
/// English-only rule is exactly what empties that shelf. The language column is what his standing rule
/// reads, so a count, its knockout and the collision are all statements about one shelf.</para>
///
/// <para><b>Only unambiguous adjectives, and only as a whole concept.</b> English adjectives
/// ("british", "american") are left out: they name a country, not a language, and every English-only
/// account would read them as no requirement at all. A phrase that the catalogue tags as a topic
/// ("korean war", "french revolution") stays a topic — the resolver checks the vocabulary first.</para>
/// </summary>
public static class LanguageWords
{
    /// <summary>Language adjective (lower case) → ISO 639-1 code as TMDB stores it.</summary>
    public static readonly IReadOnlyDictionary<string, string> Codes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["korean"] = "ko",
        ["k-drama"] = "ko",
        ["japanese"] = "ja",
        ["french"] = "fr",
        ["spanish"] = "es",
        ["italian"] = "it",
        ["german"] = "de",
        ["chinese"] = "zh",
        ["mandarin"] = "zh",
        ["cantonese"] = "cn",
        ["hindi"] = "hi",
        ["bollywood"] = "hi",
        ["swedish"] = "sv",
        ["danish"] = "da",
        ["norwegian"] = "no",
        ["finnish"] = "fi",
        ["icelandic"] = "is",
        ["russian"] = "ru",
        ["portuguese"] = "pt",
        ["brazilian"] = "pt",
        ["turkish"] = "tr",
        ["thai"] = "th",
        ["polish"] = "pl",
        ["dutch"] = "nl",
        ["persian"] = "fa",
        ["iranian"] = "fa",
        ["arabic"] = "ar",
        ["hebrew"] = "he",
        ["israeli"] = "he",
        ["indonesian"] = "id",
        ["greek"] = "el",
        ["czech"] = "cs",
        ["hungarian"] = "hu",
        ["tamil"] = "ta",
        ["telugu"] = "te",
        ["malayalam"] = "ml",
        ["vietnamese"] = "vi",
        ["tagalog"] = "tl",
        ["filipino"] = "tl",
    };

    /// <summary>The display name of a language code ("ko" → "Korean"), or the code itself when unknown.</summary>
    public static string NameOf(string code) =>
        code switch
        {
            "en" => "English",
            "ko" => "Korean",
            "ja" => "Japanese",
            "fr" => "French",
            "es" => "Spanish",
            "it" => "Italian",
            "de" => "German",
            "zh" => "Chinese",
            "cn" => "Cantonese",
            "hi" => "Hindi",
            "sv" => "Swedish",
            "da" => "Danish",
            "no" => "Norwegian",
            "fi" => "Finnish",
            "is" => "Icelandic",
            "ru" => "Russian",
            "pt" => "Portuguese",
            "tr" => "Turkish",
            "th" => "Thai",
            "pl" => "Polish",
            "nl" => "Dutch",
            "fa" => "Persian",
            "ar" => "Arabic",
            "he" => "Hebrew",
            "id" => "Indonesian",
            "el" => "Greek",
            "cs" => "Czech",
            "hu" => "Hungarian",
            "ta" => "Tamil",
            "te" => "Telugu",
            "ml" => "Malayalam",
            "vi" => "Vietnamese",
            "tl" => "Tagalog",
            _ => code,
        };

    /// <summary>The code <paramref name="word"/> names (normalised, lower case), or null.</summary>
    public static string? CodeOf(string word) => Codes.GetValueOrDefault(word);
}
