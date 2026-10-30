using System.Text.RegularExpressions;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// A source-level guard over the gateway's nginx config and the image that delivers it.
///
/// <para>
/// <c>nginx/nginx.conf</c> is the ONLY copy. <c>nginx/Dockerfile</c> bakes it into the
/// <c>reel-gateway</c> image CI publishes beside the backend and frontend, the production stack runs
/// that image, and the dev compose stack mounts the same file as its template. Until 2026-10-30
/// production carried a second, inline copy as a compose <c>configs:</c> block held byte-equal to
/// this one; that copy is gone, and <see cref="The_gateway_image_bakes_the_canonical_file"/> and
/// <see cref="Production_runs_the_published_gateway_image_and_carries_no_nginx_config_of_its_own"/>
/// are what stop a second one from coming back.
/// </para>
///
/// <para>
/// The content checks exist because a divergence was real and it was invisible. On 2026-09-10 a
/// second production descriptor (since removed) was found to be rewriting <c>/api/…</c> to <c>/…</c>
/// before it reached the backend — a <c>proxy_pass</c> with a trailing-slash URI replaces the
/// matched location prefix — so every API call on the deployed host would have 404'd, while every
/// file looked fine to a reader skimming them side by side. The same pass found that none of them
/// gave Ask Reel's stream the directives <c>/sse/</c> had, which is what makes a token-by-token
/// answer arrive as one buffered lump; and until K-8 the production copy had no resolver, so a
/// recreated backend container left the gateway proxying to a dead address.
/// </para>
///
/// <para>
/// Deliberately unit-level: it reads text off disk, touches no database and starts no host, so it
/// runs in the default <c>dotnet test</c> pass rather than behind the integration gate. What the
/// built image actually DOES to a request is <c>deploy/probe-gateway.sh</c>'s job.
/// </para>
/// </summary>
public partial class NginxDescriptorParityTests
{
    /// <summary>The one gateway config. Every content check below reads it.</summary>
    private static readonly string[] Descriptors =
    [
        "nginx/nginx.conf",
    ];

    /// <summary>The streamed locations: Ask Reel's turn and the pipeline event channel.</summary>
    private static readonly string[] StreamedLocations = ["= /api/search/ask", "/sse/"];

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
    public void The_streaming_directives_are_present_in_both_streamed_locations_everywhere()
    {
        var gaps = new List<string>();

        foreach (var descriptor in Descriptors)
        {
            var text = ReadDescriptor(descriptor);

            foreach (var location in StreamedLocations)
            {
                var block = LocationBlock(text, location);
                if (block is null)
                {
                    gaps.Add($"{descriptor}: no `location {location}` block");
                    continue;
                }

                if (!GzipOff().IsMatch(block))
                {
                    gaps.Add($"{descriptor}: `location {location}` is missing `gzip off` — a compressed stream is a buffered one");
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
            "Both streamed locations need the full streaming set in every descriptor — a stream that "
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

            foreach (var location in new[] { "/api/", "/sse/", "= /api/search/ask" })
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

    [Fact]
    public void The_gateway_image_bakes_the_canonical_file()
    {
        var root = RepositoryRoot();
        var dockerfile = File.ReadAllText(Path.Combine(root, "nginx", "Dockerfile")).Replace("\r\n", "\n", StringComparison.Ordinal);

        // The upstream nginx image renders templates/<name>.template to $NGINX_ENVSUBST_OUTPUT_DIR/<name>
        // at start; nginx.conf is a main config (events + http), so it must land on /etc/nginx/nginx.conf.
        Assert.Matches(@"(?m)^FROM nginx:", dockerfile);
        Assert.Matches(@"(?m)^COPY nginx\.conf /etc/nginx/templates/nginx\.conf\.template$", dockerfile);
        var defaults = DockerfileEnv(dockerfile);
        Assert.Equal("/etc/nginx", defaults.GetValueOrDefault("NGINX_ENVSUBST_OUTPUT_DIR"));
        // Only NGINX_* names are ever substituted, so no container variable can collide with one of
        // nginx's own $variables.
        Assert.Equal("^NGINX_", defaults.GetValueOrDefault("NGINX_ENVSUBST_FILTER"));

        // Every placeholder the template uses has a default baked in, so the image alone renders a
        // working config instead of a literal ${NGINX_BACKEND_PORT} in a proxy_pass.
        var template = File.ReadAllText(Path.Combine(root, "nginx", "nginx.conf"));
        var placeholders = Placeholder().Matches(template).Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(placeholders);
        Assert.All(placeholders, name => Assert.False(string.IsNullOrWhiteSpace(defaults.GetValueOrDefault(name)), $"nginx/Dockerfile has no ENV default for ${{{name}}}."));

        // And the defaults are the ports the stack actually listens on (backend 5000, SPA 80).
        Assert.Equal("5000", defaults["NGINX_BACKEND_PORT"]);
        Assert.Equal("80", defaults["NGINX_FRONTEND_PORT"]);
    }

    [Fact]
    public void Production_runs_the_published_gateway_image_and_carries_no_nginx_config_of_its_own()
    {
        var prod = ComposeDescriptor.Load("docker-compose.prod.yml");
        var gateway = prod.ServiceBlock("gateway");

        Assert.Contains("    image: ${DOCKERHUB_USERNAME:-frostaura}/reel-gateway:${REEL_TAG:-latest}", gateway);
        Assert.DoesNotContain(gateway, l => l.Trim() == "configs:" || l.Contains("nginx.conf", StringComparison.Ordinal));
        Assert.Null(prod.InlineConfig("gateway_nginx"));
        Assert.DoesNotMatch(@"(?m)^\s*(listen|proxy_pass|server_tokens)\b", prod.Text);

        // The dev stack renders the very same file through the same template mechanism.
        Assert.Contains(
            "      - ./nginx/nginx.conf:/etc/nginx/templates/nginx.conf.template:ro",
            ComposeDescriptor.Load("docker-compose.yml").ServiceBlock("gateway"));
    }

    [Fact]
    public void Ci_publishes_the_gateway_image_from_the_nginx_directory_with_the_backend_tag_scheme()
    {
        var workflow = File.ReadAllText(Path.Combine(RepositoryRoot(), ".github", "workflows", "ci.yml"));

        foreach (var arch in new[] { "amd64", "arm64" })
        {
            Assert.Contains($"  build-gateway-{arch}:", workflow, StringComparison.Ordinal);
            Assert.Contains($"${{{{ secrets.DOCKERHUB_USERNAME }}}}/reel-gateway:${{{{ steps.tag.outputs.tag }}}}-{arch}", workflow, StringComparison.Ordinal);
        }

        Assert.Contains("          context: ./nginx\n          file: ./nginx/Dockerfile\n", workflow.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("IMAGE: ${{ secrets.DOCKERHUB_USERNAME }}/reel-gateway", workflow, StringComparison.Ordinal);
        Assert.Matches(@"needs: \[merge-backend-manifests, merge-frontend-manifests, merge-gateway-manifests\]", workflow);
    }

    /// <summary>The <c>ENV</c> defaults a Dockerfile declares, continuation lines included.</summary>
    private static Dictionary<string, string> DockerfileEnv(string dockerfile)
    {
        var joined = Regex.Replace(dockerfile, @"\\\n\s*", " ");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match line in Regex.Matches(joined, @"(?m)^ENV\s+(.+)$"))
        {
            foreach (Match pair in Regex.Matches(line.Groups[1].Value, @"([A-Za-z_][A-Za-z0-9_]*)=(\S*)"))
            {
                result[pair.Groups[1].Value] = pair.Groups[2].Value;
            }
        }

        return result;
    }

    [GeneratedRegex(@"\$\{(NGINX_[A-Z_]+)\}")]
    private static partial Regex Placeholder();

    [Fact]
    public void The_gateway_resolves_upstreams_per_request()
    {
        // K-8: a literal upstream is resolved once at startup, so a recreated container leaves the
        // gateway proxying to a dead address with every request a 502.
        foreach (var descriptor in Descriptors)
        {
            var text = ReadDescriptor(descriptor);
            Assert.Matches(@"resolver\s+127\.0\.0\.11\b", text);
            Assert.DoesNotMatch(@"\bupstream\s+\w+\s*\{", text);
            Assert.All(ProxyPassTargets(text), target => Assert.StartsWith("http://$", target, StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_gateway_hides_its_version_compresses_sets_headers_and_refuses_source_maps()
    {
        foreach (var descriptor in Descriptors)
        {
            var text = ReadDescriptor(descriptor);
            Assert.Matches(@"server_tokens\s+off\s*;", text);
            Assert.Matches(@"gzip\s+on\s*;", text);
            Assert.Matches(@"gzip_types[^;]*application/json", text);
            Assert.DoesNotMatch(@"gzip_types[^;]*text/event-stream", text);
            foreach (var header in new[] { "X-Content-Type-Options", "X-Frame-Options", "Referrer-Policy", "Strict-Transport-Security" })
            {
                Assert.Matches(@"add_header\s+" + Regex.Escape(header) + @"\s", text);
            }

            var map = LocationBlock(text, @"~* \.map$");
            Assert.NotNull(map);
            Assert.Matches(@"return\s+404\s*;", map);
            // J-7: the visitor's scheme, not the gateway's own http.
            Assert.DoesNotMatch(@"X-Forwarded-Proto\s+\$scheme\s*;", text);
        }
    }

    [Fact]
    public void The_frontend_serves_hashed_assets_immutable_and_its_shell_uncached()
    {
        var text = Decomment(File.ReadAllText(Path.Combine(RepositoryRoot(), "src", "frontend", "nginx.conf")));

        // I-3: a missing hashed asset is a 404, never index.html with a 200.
        var assets = LocationBlock(text, "/assets/");
        Assert.NotNull(assets);
        Assert.Matches(@"try_files\s+\$uri\s+=404\s*;", assets);
        Assert.Contains("immutable", assets, StringComparison.Ordinal);

        foreach (var shell in new[] { "= /index.html", "= /sw.js" })
        {
            var block = LocationBlock(text, shell);
            Assert.NotNull(block);
            Assert.Matches(@"Cache-Control\s+""no-cache""", block);
        }

        Assert.Matches(@"server_tokens\s+off\s*;", text);
        Assert.Matches(@"gzip\s+on\s*;", text);
        Assert.NotNull(LocationBlock(text, @"~* \.map$"));
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

    [GeneratedRegex(@"gzip\s+off\s*;")]
    private static partial Regex GzipOff();

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
