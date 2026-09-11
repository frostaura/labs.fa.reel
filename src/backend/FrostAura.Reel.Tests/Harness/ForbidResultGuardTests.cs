using System.Text.RegularExpressions;

namespace FrostAura.Reel.Tests.Harness;

/// <summary>
/// A source-level guard against one specific way of answering 403 that cannot work in this app.
///
/// <para><c>Results.Forbid()</c> compiles, reads correctly, and is what every ASP.NET Core sample
/// shows. In this app it throws: it calls <c>HttpContext.ForbidAsync()</c>, which resolves
/// <c>IAuthenticationService</c> from the container and dispatches to the default forbid scheme —
/// and this app registers no authentication handler at all, because identity is the <c>reel_at</c>
/// cookie resolved by <c>AccountResolutionMiddleware</c>. The request ends 500
/// (<c>InvalidOperationException: Unable to find the required 'IAuthenticationService' service</c>),
/// not 403.</para>
///
/// <para>The behavioural proof lives in <c>IntegrationTests/TierGateTests.cs</c>, which needs a
/// database and a host. This is the cheap half: nothing in the endpoint layer may reintroduce the
/// call at all. It exists because the failure is invisible in every way that normally catches a
/// defect — it compiles, the analysers are silent, and the deny branch is only reachable by an
/// account that is not the founder, which is the one account nobody develops against.</para>
///
/// <para>The house answer is <c>EndpointHelpers.Forbidden</c>. If a future change genuinely adds an
/// authentication scheme, delete this guard rather than allowlisting around it — with a scheme
/// registered, <c>Results.Forbid()</c> becomes correct again and a stale guard would be banning a
/// working call.</para>
/// </summary>
public class ForbidResultGuardTests
{
    /// <summary>
    /// <c>Results.Forbid</c> or <c>TypedResults.Forbid</c>, with any argument list. The argument
    /// list is deliberately not required to be empty: the overloads that take
    /// <c>AuthenticationProperties</c> or a scheme list go down exactly the same
    /// <c>ForbidAsync</c> path and fail identically.
    /// </summary>
    private static readonly Regex ForbidCall =
        new(@"\b(?:Typed)?Results\.Forbid\s*\(", RegexOptions.Compiled);

    [Fact]
    public void No_endpoint_returns_Results_Forbid()
    {
        var endpoints = Path.Combine(ApiRoot(), "Endpoints");
        var findings = new List<string>();

        foreach (var file in Directory
            .EnumerateFiles(endpoints, "*.cs", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal))
        {
            // Reuse the comment-blanking from the culture guard so the explanatory prose in
            // EndpointHelpers — which necessarily names the call it is warning about — is not
            // itself a finding.
            var scannable = CultureSensitiveFormattingGuardTests.Scannable(File.ReadAllText(file));
            foreach (Match match in ForbidCall.Matches(scannable))
            {
                var line = scannable.Take(match.Index).Count(c => c == '\n') + 1;
                findings.Add($"{Path.GetFileName(file)}:{line}  {match.Value.Trim()}");
            }
        }

        Assert.True(
            findings.Count == 0,
            "Results.Forbid() reached the endpoint layer. It throws in this app — there is no "
            + "IAuthenticationService registered, so ForbidAsync has nothing to dispatch to and the "
            + "request ends 500 rather than 403. Return EndpointHelpers.Forbidden(\"…\") instead.\n\n"
            + string.Join("\n", findings));
    }

    [Fact]
    public void The_scanner_actually_catches_the_shapes_it_claims_to()
    {
        // Without this the guard could silently degrade to matching nothing.
        Assert.Matches(ForbidCall, "return Results.Forbid();");
        Assert.Matches(ForbidCall, "return TypedResults.Forbid();");
        Assert.Matches(ForbidCall, "return Results.Forbid(properties, [\"scheme\"]);");

        // And that it does not fire on the replacement, nor on prose about the banned call.
        Assert.DoesNotMatch(ForbidCall, "return EndpointHelpers.Forbidden(\"founder only\");");
        Assert.Empty(ForbidCall.Matches(
            CultureSensitiveFormattingGuardTests.Scannable("// Results.Forbid() is why this exists")));
    }

    /// <summary>The API project directory, found by walking up from the test binary.</summary>
    private static string ApiRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "FrostAura.Reel.Api")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "FrostAura.Reel.Api");
    }
}
