using System.Text.RegularExpressions;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// A source-level guard over the three nginx descriptors this repository ships.
///
/// <para>
/// There are three of them and there is no way to make there be one. <c>nginx/nginx.conf</c> is a
/// real file mounted by the dev compose stack; <c>docker-compose.prod.yml</c> and
/// <c>deploy/portainer-stack.yml</c> inline their gateway config as a compose <c>configs:</c>
/// block, because a Portainer stack cannot mount a file out of a repository it never clones. So
/// "one source of truth" here means one CANONICAL file — <c>nginx/nginx.conf</c> — plus this
/// check, rather than a deletion.
/// </para>
///
/// <para>
/// The check exists because the divergence was real and it was invisible. On 2026-09-10 the
/// Portainer descriptor was found to be rewriting <c>/api/…</c> to <c>/…</c> before it reached the
/// backend — a <c>proxy_pass</c> with a trailing-slash URI replaces the matched location prefix —
/// so every API call on the deployed host would have 404'd, while all three files looked fine to
/// a reader skimming them side by side. The same pass found that none of the three gave
/// <c>/api/</c> the streaming directives <c>/sse/</c> had, which is what makes a token-by-token
/// Ask Reel stream arrive as one buffered lump.
/// </para>
///
/// <para>
/// Deliberately unit-level: it reads text off disk, touches no database and starts no host, so it
/// runs in the default <c>dotnet test</c> pass rather than behind the integration gate.
/// </para>
/// </summary>
public class NginxDescriptorParityTests
{
    /// <summary>
    /// Repository-relative paths of every descriptor that configures the gateway. The canonical
    /// one is first; the other two are inline copies that must not drift from it.
    /// </summary>
    private static readonly string[] Descriptors =
    [
        "nginx/nginx.conf",
        "docker-compose.prod.yml",
        "deploy/portainer-stack.yml",
    ];

    /// <summary>
    /// The six directives that decide whether a response streams or arrives as one lump. They are
    /// matched by NAME, not by value, because the two timeouts legitimately differ — a chat turn is
    /// bounded at an hour, a pipeline event channel at a day.
    /// </summary>
    private static readonly (string Name, Regex Pattern)[] StreamingDirectives =
    [
        ("proxy_http_version 1.1", new Regex(@"proxy_http_version\s+1\.1\s*;", RegexOptions.Compiled)),
        ("proxy_set_header Connection ''", new Regex(@"proxy_set_header\s+Connection\s+(?:''|"""")\s*;", RegexOptions.Compiled)),
        ("chunked_transfer_encoding off", new Regex(@"chunked_transfer_encoding\s+off\s*;", RegexOptions.Compiled)),
        ("proxy_buffering off", new Regex(@"proxy_buffering\s+off\s*;", RegexOptions.Compiled)),
        ("proxy_cache off", new Regex(@"proxy_cache\s+off\s*;", RegexOptions.Compiled)),
        ("proxy_read_timeout", new Regex(@"proxy_read_timeout\s+\d+s\s*;", RegexOptions.Compiled)),
    ];

    [Fact]
    public void Every_descriptor_routes_both_the_api_and_the_sse_prefix()
    {
        var missing = new List<string>();

        foreach (var descriptor in Descriptors)
        {
            var text = ReadDescriptor(descriptor);

            foreach (var location in new[] { "/api/", "/sse/" })
            {
                if (LocationBlock(text, location) is null)
                {
                    missing.Add($"{descriptor}: no `location {location}` block");
                }
            }
        }

        Assert.True(
            missing.Count == 0,
            "Every gateway descriptor must route both prefixes to the backend. Missing:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, missing));
    }

    [Fact]
    public void No_api_proxy_pass_strips_the_api_prefix()
    {
        var offenders = new List<string>();

        foreach (var descriptor in Descriptors)
        {
            var block = LocationBlock(ReadDescriptor(descriptor), "/api/");
            if (block is null)
            {
                continue; // Covered by the routing test; not this one's finding to report.
            }

            foreach (var target in ProxyPassTargets(block))
            {
                // A proxy_pass carrying a URI replaces the matched location prefix with that URI.
                // Two shapes are safe: no URI at all (nginx forwards the original request URI), or
                // an explicit $request_uri, which carries the original path AND query string. A
                // bare trailing slash is the shape that silently deletes `/api`.
                if (target.EndsWith('/') && !target.Contains("$request_uri", StringComparison.Ordinal))
                {
                    offenders.Add($"{descriptor}: `proxy_pass {target};` rewrites /api/… to /…");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "A trailing-slash proxy_pass URI strips the location prefix, so the backend never sees "
            + "/api and answers 404 to everything:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void The_streaming_directives_are_present_in_both_proxied_locations_everywhere()
    {
        var gaps = new List<string>();

        foreach (var descriptor in Descriptors)
        {
            var text = ReadDescriptor(descriptor);

            foreach (var location in new[] { "/api/", "/sse/" })
            {
                var block = LocationBlock(text, location);
                if (block is null)
                {
                    continue; // Covered by the routing test.
                }

                foreach (var (name, pattern) in StreamingDirectives)
                {
                    if (!pattern.IsMatch(block))
                    {
                        gaps.Add($"{descriptor}: `location {location}` is missing `{name}`");
                    }
                }
            }
        }

        Assert.True(
            gaps.Count == 0,
            "Both proxied locations need the full streaming set in every descriptor — a stream that "
            + "buffers at the gateway arrives as one lump however well the server chunks it:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, gaps));
    }

    [Fact]
    public void Every_descriptor_stamps_and_logs_one_request_id_per_proxied_request()
    {
        // R1-36: the gateway's access-log line and the API's log lines for one request share one id
        // only if the gateway both forwards its own $request_id and writes it to the log it keeps.
        var forwards = new Regex(@"proxy_set_header\s+X-Request-Id\s+\$request_id\s*;");
        var gaps = new List<string>();

        foreach (var descriptor in Descriptors)
        {
            var text = ReadDescriptor(descriptor);

            foreach (var location in new[] { "/api/", "/sse/" })
            {
                if (LocationBlock(text, location) is { } block && !forwards.IsMatch(block))
                {
                    gaps.Add($"{descriptor}: `location {location}` does not forward `X-Request-Id $request_id`");
                }
            }

            var format = new Regex(@"log_format\s+main\s+([^;]+);").Match(text);
            if (!format.Success || !format.Groups[1].Value.Contains("$request_id", StringComparison.Ordinal))
            {
                gaps.Add($"{descriptor}: no `log_format main` carrying $request_id");
            }

            if (!new Regex(@"access_log\s+\S+\s+main\s*;").IsMatch(text))
            {
                gaps.Add($"{descriptor}: the access log does not use the `main` format");
            }
        }

        Assert.True(gaps.Count == 0, string.Join(Environment.NewLine, gaps));
    }

    [Fact]
    public void Every_descriptor_proxies_the_readiness_probe()
    {
        var missing = Descriptors
            .Where(d => LocationBlock(ReadDescriptor(d), "/health/ready") is null)
            .ToList();

        Assert.True(
            missing.Count == 0,
            "The compose healthchecks call /health/ready in-container, but an operator and any "
            + "external monitor reach it through the gateway. Not proxied by: "
            + string.Join(", ", missing));
    }

    /// <summary>
    /// The block reader is the only clever part of this file, so it is probed directly: an
    /// extractor that silently returned nothing would make every assertion above vacuously true.
    /// </summary>
    [Fact]
    public void The_block_reader_finds_what_it_claims_to()
    {
        const string sample = """
            http {
              server {
                location /api/ {
                  proxy_pass http://backend_upstream;
                  proxy_buffering off;
                }
                location /health { proxy_pass http://backend_upstream; }
                location /health/ready { proxy_pass http://backend_upstream; }
                location / { proxy_pass http://frontend_upstream; }
              }
            }
            """;

        var api = LocationBlock(sample, "/api/");
        Assert.NotNull(api);
        Assert.Contains("proxy_buffering off;", api, StringComparison.Ordinal);
        Assert.DoesNotContain("frontend_upstream", api, StringComparison.Ordinal);

        // `location /health` must not swallow `location /health/ready` or vice versa.
        Assert.NotNull(LocationBlock(sample, "/health"));
        Assert.NotNull(LocationBlock(sample, "/health/ready"));
        Assert.DoesNotContain("ready", LocationBlock(sample, "/health")!, StringComparison.Ordinal);

        Assert.Null(LocationBlock(sample, "/sse/"));
        Assert.Equal(["http://backend_upstream"], ProxyPassTargets(api!));

        // The compose descriptors escape nginx runtime variables as `$$`; the reader un-escapes so
        // a `$request_uri` written for compose is still recognised as one.
        Assert.Contains(
            "$request_uri",
            Unescape("proxy_pass http://backend:5000$$request_uri;"),
            StringComparison.Ordinal);

        // A comment is not a directive, in either direction: one describing the trailing-slash bug
        // must not read as the bug, and a commented-out directive must not read as present.
        Assert.DoesNotContain(
            "proxy_pass",
            Decomment("  # the bad shape was proxy_pass http://backend:5000/;"),
            StringComparison.Ordinal);
        Assert.Contains(
            "proxy_buffering off;",
            Decomment("  proxy_buffering off;  # SSE"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads a descriptor, normalises compose's `$$` escape back to a single `$`, and strips
    /// comments. The inline descriptors live inside a YAML block scalar, but nginx braces balance
    /// there exactly as they do in a real config file, so no YAML parser is needed to find a
    /// location block.
    ///
    /// <para>
    /// Comments are stripped because both directions of this check are otherwise wrong: a comment
    /// EXPLAINING the trailing-slash bug reads as the bug (it did, the first time this test ran
    /// against the fix), and a commented-out directive would read as a present one. nginx and YAML
    /// share the `#`-to-end-of-line form, so one rule covers all three files.
    /// </para>
    /// </summary>
    private static string ReadDescriptor(string relativePath)
    {
        var full = Path.Combine(RepositoryRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Descriptor not found: {relativePath}");
        return Decomment(Unescape(File.ReadAllText(full)));
    }

    private static string Unescape(string text) => text.Replace("$$", "$", StringComparison.Ordinal);

    private static string Decomment(string text) =>
        string.Join(
            Environment.NewLine,
            text.Split('\n').Select(line => line.Split('#', 2)[0]));

    /// <summary>
    /// Returns the body of <c>location {prefix} { … }</c>, braces included, or null when the
    /// location is absent. Matching is exact on the prefix, so <c>/health</c> and
    /// <c>/health/ready</c> are two different locations rather than one.
    /// </summary>
    private static string? LocationBlock(string text, string prefix)
    {
        var opener = new Regex(@"location\s+" + Regex.Escape(prefix) + @"\s*\{");
        var match = opener.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var start = match.Index + match.Length - 1;
        var depth = 0;

        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[start..(i + 1)];
                }
            }
        }

        Assert.Fail($"Unbalanced braces after `location {prefix}`.");
        return null;
    }

    private static string[] ProxyPassTargets(string block) =>
        [.. new Regex(@"proxy_pass\s+([^;]+);")
            .Matches(block)
            .Select(m => m.Groups[1].Value.Trim())];

    /// <summary>Walks up from the test binary to the repository root (the directory holding <c>nginx/</c>).</summary>
    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "nginx", "nginx.conf")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }
}
