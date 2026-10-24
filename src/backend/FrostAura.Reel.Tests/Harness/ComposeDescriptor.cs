using System.Text.RegularExpressions;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// Just enough of a compose-file reader for the descriptor guards: services, each service's
/// <c>environment:</c> keys (map or list form), <c>ports:</c>, whether it logs through the shared
/// anchor, and the body of an inline <c>configs:</c> entry. Line-based on purpose — the descriptors
/// are written in one house style, and a guard that needs a YAML library to read them would be the
/// first thing in the test project that does.
/// </summary>
internal sealed partial class ComposeDescriptor
{
    private readonly string[] lines;

    private ComposeDescriptor(string relativePath, string text)
    {
        RelativePath = relativePath;
        Text = text;
        lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    }

    public string RelativePath { get; }

    public string Text { get; }

    /// <summary>Loads a descriptor by repository-relative path.</summary>
    public static ComposeDescriptor Load(string relativePath) =>
        new(relativePath, File.ReadAllText(Path.Combine(RepositoryRoot(), relativePath)));

    /// <summary>True when the repository holds this descriptor.</summary>
    public static bool Exists(string relativePath) => File.Exists(Path.Combine(RepositoryRoot(), relativePath));

    /// <summary>Service names under the top-level <c>services:</c> key, in file order.</summary>
    public IReadOnlyList<string> Services =>
        [.. TopLevelBlock("services").Select(l => ServiceName().Match(l)).Where(m => m.Success).Select(m => m.Groups[1].Value)];

    /// <summary>The <c>environment:</c> entries of one service, as key → raw value text.</summary>
    public IReadOnlyDictionary<string, string> Environment(string service)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in ServiceSection(service, "environment"))
        {
            var map = MapEntry().Match(line);
            if (map.Success)
            {
                result[map.Groups[1].Value] = map.Groups[2].Value.Trim();
                continue;
            }

            var list = ListEntry().Match(line);
            if (list.Success)
            {
                result[list.Groups[1].Value] = list.Groups[2].Value.Trim();
            }
        }

        return result;
    }

    /// <summary>The <c>ports:</c> entries of one service, unquoted.</summary>
    public IReadOnlyList<string> Ports(string service) =>
        [.. ServiceSection(service, "ports")
            .Select(l => PortEntry().Match(l))
            .Where(m => m.Success)
            .Select(m => m.Groups[1].Value.Trim().Trim('"'))];

    /// <summary>The raw lines of one service's block, its own header excluded.</summary>
    public IReadOnlyList<string> ServiceBlock(string service)
    {
        var block = TopLevelBlock("services");
        var start = block.FindIndex(l => l == $"  {service}:");
        if (start < 0)
        {
            return [];
        }

        var end = block.FindIndex(start + 1, l => ServiceName().IsMatch(l));
        return block.GetRange(start + 1, (end < 0 ? block.Count : end) - start - 1);
    }

    /// <summary>The body of <c>configs: name: content: |</c>, de-indented, or null when absent.</summary>
    public string? InlineConfig(string name)
    {
        var block = TopLevelBlock("configs");
        var start = block.FindIndex(l => l == $"  {name}:");
        if (start < 0 || start + 1 >= block.Count || block[start + 1].Trim() != "content: |")
        {
            return null;
        }

        var body = new List<string>();
        for (var i = start + 2; i < block.Count; i++)
        {
            var line = block[i];
            if (line.Length == 0)
            {
                body.Add(string.Empty);
                continue;
            }

            if (!line.StartsWith("      ", StringComparison.Ordinal))
            {
                break;
            }

            body.Add(line[6..]);
        }

        while (body.Count > 0 && body[^1].Length == 0)
        {
            body.RemoveAt(body.Count - 1);
        }

        return string.Join("\n", body) + "\n";
    }

    /// <summary>Lines of a top-level key's block (indented children only).</summary>
    public List<string> TopLevelBlock(string key)
    {
        var start = Array.IndexOf(lines, key + ":");
        if (start < 0)
        {
            return [];
        }

        var block = new List<string>();
        for (var i = start + 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#'))
            {
                break;
            }

            block.Add(line);
        }

        return block;
    }

    private IEnumerable<string> ServiceSection(string service, string section)
    {
        var block = ServiceBlock(service);
        var start = -1;
        for (var i = 0; i < block.Count; i++)
        {
            if (block[i] == $"    {section}:")
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            yield break;
        }

        for (var i = start + 1; i < block.Count; i++)
        {
            var line = block[i];
            if (line.TrimStart().StartsWith('#') || line.Length == 0)
            {
                continue;
            }

            if (!line.StartsWith("      ", StringComparison.Ordinal))
            {
                yield break;
            }

            yield return line;
        }
    }

    /// <summary>Walks up from the test binary to the repository root (the directory holding <c>fa.reel.slnx</c>).</summary>
    public static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "fa.reel.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }

    [GeneratedRegex(@"^  ([a-z][a-z0-9_-]*):\s*$")]
    private static partial Regex ServiceName();

    [GeneratedRegex(@"^      ([A-Za-z_][A-Za-z0-9_]*):(.*)$")]
    private static partial Regex MapEntry();

    [GeneratedRegex(@"^      - ([A-Za-z_][A-Za-z0-9_]*)=(.*)$")]
    private static partial Regex ListEntry();

    [GeneratedRegex(@"^      - (.+)$")]
    private static partial Regex PortEntry();
}
