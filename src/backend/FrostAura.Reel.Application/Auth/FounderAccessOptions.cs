using System.Text.RegularExpressions;
using FrostAura.Reel.Application.Configuration;

namespace FrostAura.Reel.Application.Auth;

/// <summary>
/// <c>FOUNDER_TRAKT_SLUGS</c>: the Trakt profiles whose sign-in grants the Founder tier (K-10, FD-11).
///
/// <para><b>Why it exists.</b> The only line that ever assigned Founder was the QA sign-in
/// (<c>/api/auth/dev/link</c>), which is Development-only and loopback-only. A Production stack
/// therefore signed the founder in as Free — the three-pick shortlist with rows stripped — and
/// nothing short of a hand-written UPDATE could change it.</para>
///
/// <para><b>What it does and does not do.</b> A listed slug is raised to Founder on every sign-in
/// (new account or returning). Removing a slug does NOT demote: Founder is also granted by hand
/// during the beta, and a config edit that silently stripped a tier would be a worse surprise than
/// a stale grant. Demotion is a deliberate database change, documented in
/// <c>docs/deployment.md</c>.</para>
///
/// <para>Comma- or whitespace-separated, compared case-insensitively. Trakt slugs are lowercase
/// letters, digits, <c>-</c>, <c>_</c> and <c>.</c>; anything else stops the host at startup,
/// because a typo here is a founder who quietly signs in as Free.</para>
/// </summary>
public sealed partial class FounderAccessOptions : ValidatedOptions
{
    public const string SlugsKey = "FOUNDER_TRAKT_SLUGS";

    private HashSet<string> slugs = new(StringComparer.OrdinalIgnoreCase);

    private int invalid;

    /// <summary>The configured slugs, lowercased; empty when the key is unset.</summary>
    public IReadOnlySet<string> Slugs => slugs;

    /// <summary>Whether a sign-in by <paramref name="traktSlug"/> is granted the Founder tier.</summary>
    public bool IsFounder(string? traktSlug) =>
        traktSlug is { Length: > 0 } slug && slugs.Contains(slug.Trim());

    protected override void Read(SettingReader reader)
    {
        slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        invalid = 0;
        var raw = reader.Raw(SlugsKey);
        if (raw is null)
        {
            return;
        }

        foreach (var part in raw.Split([',', ' ', '\t', '\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries))
        {
            var slug = part.Trim().ToLowerInvariant();
            if (SlugShape().IsMatch(slug))
            {
                slugs.Add(slug);
            }
            else
            {
                invalid++;
            }
        }
    }

    protected override void Check(Action<string> problem)
    {
        if (invalid > 0)
        {
            // The entries are not echoed: problems are logged, and a mis-pasted secret is not a slug.
            problem(
                $"{SlugsKey} holds {invalid.ToString(System.Globalization.CultureInfo.InvariantCulture)} entr{(invalid == 1 ? "y" : "ies")} "
                + "that cannot be a Trakt slug (lowercase letters, digits, '-', '_' or '.', at most 64 characters).");
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugShape();
}
