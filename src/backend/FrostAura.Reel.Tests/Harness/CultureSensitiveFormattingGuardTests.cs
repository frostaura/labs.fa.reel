using System.Text;
using System.Text.RegularExpressions;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// A source-level guard against the one defect class the compiler, the analysers and the test
/// suite all miss: a number formatted into a machine-read string under the host's culture.
///
/// <para>
/// Roslyn cannot help here and neither can CA1305. That rule fires on <c>ToString()</c> and
/// <c>Convert.To*</c> overloads, but says NOTHING about <c>$"…{someDecimal:F3}…"</c> — which is
/// exactly the shape every real site in this repository took. The suite cannot help either: the
/// dev Mac and CI both run cultures where the bug is invisible until the string reaches TMDB, or
/// Postgres, or a cache key comparison. So the check is textual, and deliberately so.
/// </para>
///
/// <para>
/// It is a RATCHET, not a verdict. <see cref="Vetted"/> holds the sites that were looked at and
/// judged safe, each with the reason; the test fails only on something new. Adding a row is
/// therefore a decision someone has to write a sentence about, and the sentence is the point.
/// </para>
///
/// <para>
/// What it deliberately does not scan: structured-logging templates (Microsoft.Extensions.Logging
/// formats those with the invariant culture itself, in <c>FormattedLogValues</c>), anything
/// already wrapped in an invariant formatter, comments, and the test project.
/// </para>
/// </summary>
public class CultureSensitiveFormattingGuardTests
{
    private static readonly string[] ScannedProjects =
    [
        "FrostAura.Reel.Api",
        "FrostAura.Reel.Application",
        "FrostAura.Reel.Domain",
        "FrostAura.Reel.Infrastructure",
    ];

    /// <summary>
    /// Sites that have been read and judged safe, keyed <c>relative/path.cs::matched-text</c> so a
    /// row survives the line moving. Every entry carries WHY — an allowlist without reasons is just
    /// a suppression file, and the next reader has no way to tell a vetted site from a tired one.
    /// </summary>
    private static readonly Dictionary<string, string> Vetted = new(StringComparer.Ordinal)
    {
        ["FrostAura.Reel.Application/Search/EligibilityQueryBuilder.cs::Convert.ToDecimal("] =
            "Inside an EF query tree: translated to a Postgres CAST, never executed in .NET. The "
            + "IFormatProvider overload is untranslatable and takes the whole predicate down with it.",
        ["FrostAura.Reel.Application/Search/EligibilityQueryBuilder.cs::Convert.ToInt32("] =
            "Same as Convert.ToDecimal above — a SQL CAST, not a .NET conversion.",
    };

    /// <summary>
    /// The risky shapes. Each is a regex over source with the safe regions already removed, so a
    /// hit is either a real leak or something to vet and add to <see cref="Vetted"/>.
    /// </summary>
    private static readonly (string Name, Regex Pattern)[] Risky =
    [
        // $"…{x:F3}…", {x:N0}, {x:P1}, {x:0.0}, {x:#,##0.0} — a numeric format specifier inside an
        // interpolation hole that no invariant formatter is wrapping. A letter specifier must carry
        // a precision digit and a custom one must carry a decimal or group separator, so the Guid
        // shapes ({id:N}, {id:D}) stay out: they are culture-independent and pervasive here. The
        // cost of that exclusion is that a bare {someDecimal:N} slips through, which is a shape
        // this codebase does not use — a false negative, and false negatives are the safe side.
        ("interpolated numeric format", new Regex(@"\{[^{}""]+:(?:[FfNnPpEeCc]\d+|[#0][#0,]*[.,][#0]*)\}", RegexOptions.Compiled)),
        // .ToString("0.0") / .ToString("F3") with no provider argument. Same specifier rule, so
        // Guid.ToString("N") is not a finding.
        ("ToString(format) without a provider", new Regex(@"\.ToString\(\s*""(?:[FfNnPpEeCc]\d+|[#0][#0,]*[.,][#0]*)""\s*\)", RegexOptions.Compiled)),
        // string.Format with a literal template — the provider overload takes it first.
        ("string.Format without a provider", new Regex(@"string\.Format\(\s*""", RegexOptions.Compiled)),
        // Numeric Convert/Parse without a provider.
        ("Convert.ToDecimal(", new Regex(@"Convert\.ToDecimal\([^,()]*\)", RegexOptions.Compiled)),
        ("Convert.ToDouble(", new Regex(@"Convert\.ToDouble\([^,()]*\)", RegexOptions.Compiled)),
        ("Convert.ToSingle(", new Regex(@"Convert\.ToSingle\([^,()]*\)", RegexOptions.Compiled)),
        ("Convert.ToInt32(", new Regex(@"Convert\.ToInt32\([^,()]*\)", RegexOptions.Compiled)),
        ("decimal.Parse without a provider", new Regex(@"\bdecimal\.Parse\([^,()]*\)", RegexOptions.Compiled)),
        ("double.Parse without a provider", new Regex(@"\bdouble\.Parse\([^,()]*\)", RegexOptions.Compiled)),
        ("decimal.TryParse without a provider", new Regex(@"\bdecimal\.TryParse\([^,()]*,\s*out\b", RegexOptions.Compiled)),
        ("double.TryParse without a provider", new Regex(@"\bdouble\.TryParse\([^,()]*,\s*out\b", RegexOptions.Compiled)),
    ];

    /// <summary>
    /// Call heads whose whole argument list is safe by construction, blanked before scanning.
    /// The logging ones are safe because <c>FormattedLogValues</c> formats message templates with
    /// <see cref="System.Globalization.CultureInfo.InvariantCulture"/> itself — a structured log
    /// template is not a culture bug, and treating it as one would bury the real findings.
    /// <c>string.Format(</c> whose first argument is not a literal is the provider overload.
    /// </summary>
    private static readonly Regex[] SafeCallHeads =
    [
        new(@"\bLog(?:Trace|Debug|Information|Warning|Error|Critical)\s*\(", RegexOptions.Compiled),
        new(@"\bstring\.Create\s*\(", RegexOptions.Compiled),
        new(@"\bFormattableString\.Invariant\s*\(", RegexOptions.Compiled),
        new(@"\bstring\.Format\s*\(\s*(?!"")", RegexOptions.Compiled),
    ];

    [Fact]
    public void No_unvetted_culture_sensitive_formatting_reaches_the_backend_sources()
    {
        var root = BackendRoot();
        var findings = new List<string>();

        foreach (var file in ScannedProjects
            .Select(p => Path.Combine(root, p))
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            var scannable = Scannable(File.ReadAllText(file));

            foreach (var (name, pattern) in Risky)
            {
                foreach (Match match in pattern.Matches(scannable))
                {
                    var key = $"{relative}::{Key(name, match.Value)}";
                    if (Vetted.ContainsKey(key))
                    {
                        continue;
                    }

                    var line = scannable.Take(match.Index).Count(c => c == '\n') + 1;
                    findings.Add($"{relative}:{line}  [{name}]  {match.Value.Trim()}");
                }
            }
        }

        Assert.True(
            findings.Count == 0,
            "Culture-sensitive formatting reached the backend sources. Each of these formats a number "
            + "into a string that something downstream parses or compares exactly — a TMDB URL, a "
            + "prompt, a cache key, a stored filter row, an SSE payload. Wrap it in "
            + "string.Create(CultureInfo.InvariantCulture, $\"…\") or FormattableString.Invariant(…), "
            + "or add it to CultureSensitiveFormattingGuardTests.Vetted WITH THE REASON.\n\n"
            + string.Join("\n", findings));
    }

    [Fact]
    public void Every_vetted_row_still_points_at_something_real()
    {
        // A stale allowlist is worse than none: it reads as "someone checked this", and nobody has.
        var root = BackendRoot();
        var dead = Vetted.Keys
            .Where(k => !File.Exists(Path.Combine(root, k.Split("::", 2)[0].Replace('/', Path.DirectorySeparatorChar))))
            .ToList();

        Assert.Empty(dead);
    }

    [Fact]
    public void The_scanner_actually_catches_the_shapes_it_claims_to()
    {
        // Without this the guard could silently degrade to matching nothing — the classic way a
        // grep-based test goes green forever.
        Assert.Contains(Risky, r => r.Pattern.IsMatch("var s = $\"NDCG {x:F3} done\";"));
        Assert.Contains(Risky, r => r.Pattern.IsMatch("var s = $\"count {n:N0}\";"));
        Assert.Contains(Risky, r => r.Pattern.IsMatch("var s = $\"floor {f:0.#}\";"));
        Assert.Contains(Risky, r => r.Pattern.IsMatch("var s = value.ToString(\"0.0\");"));
        Assert.Contains(Risky, r => r.Pattern.IsMatch("var s = string.Format(\"{0}\", x);"));
        Assert.Contains(Risky, r => r.Pattern.IsMatch("var d = decimal.Parse(raw);"));
        Assert.Contains(Risky, r => r.Pattern.IsMatch("if (decimal.TryParse(raw, out var v)) { }"));

        // And that the safe shapes really are invisible to it once the safe regions are removed.
        Assert.Empty(Findings(Scannable("var s = string.Create(CultureInfo.InvariantCulture, $\"NDCG {x:F3}\");")));
        Assert.Empty(Findings(Scannable("var s = string.Format(\n    CultureInfo.InvariantCulture,\n    \"{0:0.#}\", x);")));
        Assert.Empty(Findings(Scannable("var s = $\"{id:N}\";")));
        Assert.Empty(Findings(Scannable("var s = FormattableString.Invariant($\"NDCG {x:F3}\");")));
        Assert.Empty(Findings(Scannable("var s = value.ToString(\"0.0\", CultureInfo.InvariantCulture);")));
        Assert.Empty(Findings(Scannable("logger.LogInformation(\"ndcg={Ndcg:F3}\", ndcg);")));
        Assert.Empty(Findings(Scannable("// a comment mentioning $\"{x:F3}\" is not code")));
    }

    private static List<string> Findings(string scannable) =>
        Risky.SelectMany(r => r.Pattern.Matches(scannable).Select(m => $"[{r.Name}] {m.Value}")).ToList();

    /// <summary>
    /// The stable half of an allowlist key. For the shapes whose match text is the call itself the
    /// call is the key; for the pattern-matched ones the pattern name is, because the matched text
    /// carries the variable name and would churn on a rename.
    /// </summary>
    private static string Key(string name, string matched) =>
        name.EndsWith('(') ? name : $"{name}::{matched.Trim()}";

    /// <summary>
    /// The source with every region that CANNOT carry the defect blanked out (replaced by spaces,
    /// so line numbers survive): comments, verbatim/raw string literals, structured-logging calls,
    /// and anything already wrapped in an invariant formatter.
    /// </summary>
    internal static string Scannable(string source)
    {
        var text = new StringBuilder(source);

        BlankLineComments(text);
        BlankBlockComments(text);
        foreach (var head in SafeCallHeads)
        {
            BlankCalls(text, head);
        }

        return text.ToString();
    }

    private static void BlankLineComments(StringBuilder text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (text[i] != '/' || text[i + 1] != '/')
            {
                continue;
            }

            while (i < text.Length && text[i] != '\n')
            {
                text[i++] = ' ';
            }
        }
    }

    private static void BlankBlockComments(StringBuilder text)
    {
        var source = text.ToString();
        var start = source.IndexOf("/*", StringComparison.Ordinal);
        while (start >= 0)
        {
            var end = source.IndexOf("*/", start + 2, StringComparison.Ordinal);
            var stop = end < 0 ? source.Length : end + 2;
            for (var i = start; i < stop; i++)
            {
                if (text[i] != '\n')
                {
                    text[i] = ' ';
                }
            }

            start = end < 0 ? -1 : source.IndexOf("/*", stop, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// Blanks each named call and its balanced argument list. Crude on purpose: over-blanking makes
    /// the guard miss a site (which the reviewer catches), under-blanking makes it cry wolf on safe
    /// code (which trains everyone to add allowlist rows without reading them). Miss beats wolf.
    /// </summary>
    private static void BlankCalls(StringBuilder text, Regex head)
    {
        var source = text.ToString();
        var searchFrom = 0;
        while (searchFrom < source.Length && head.Match(source, searchFrom) is { Success: true } match)
        {
            var open = source.IndexOf('(', match.Index);
            if (open < 0)
            {
                break;
            }

            var depth = 0;
            var i = open;
            for (; i < source.Length; i++)
            {
                if (source[i] == '(')
                {
                    depth++;
                }
                else if (source[i] == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }
            }

            var stop = Math.Min(source.Length, i + 1);
            for (var j = match.Index; j < stop; j++)
            {
                if (text[j] != '\n')
                {
                    text[j] = ' ';
                }
            }

            source = text.ToString();
            searchFrom = stop;
        }
    }

    /// <summary>Walks up from the test binary to <c>src/backend</c>.</summary>
    private static string BackendRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "FrostAura.Reel.Application")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }
}
