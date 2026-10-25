using FrostAura.Reel.Application.Configuration;

namespace FrostAura.Reel.Application.Auth;

/// <summary>
/// App-session settings (fa.startup naming), validated at startup. The first
/// <see cref="ValidatedOptions"/> class and the worked example of the pattern.
///
/// <para>A missing <c>JWT_SECRET</c> used to surface as a 500 on the first sign-in, and an
/// unparseable expiry silently became the default; both now stop the host before it listens.</para>
/// </summary>
public sealed class SessionOptions : ValidatedOptions
{
    public const string SecretKey = "JWT_SECRET";
    public const string IssuerKey = "JWT_ISSUER";
    public const string AudienceKey = "JWT_AUDIENCE";
    public const string AccessMinutesKey = "JWT_ACCESS_TOKEN_EXPIRY_MINUTES";
    public const string RefreshDaysKey = "JWT_REFRESH_TOKEN_EXPIRY_DAYS";

    /// <summary>The signing secret for session JWTs and the OAuth state codec.</summary>
    public string Secret { get; private set; } = string.Empty;

    public string Issuer { get; private set; } = "frostaura-reel";

    public string Audience { get; private set; } = "frostaura-reel-app";

    /// <summary>Access-token lifetime, 1 minute to 1 day.</summary>
    public int AccessMinutes { get; private set; } = 15;

    /// <summary>Refresh-session lifetime, 1 to 365 days.</summary>
    public int RefreshDays { get; private set; } = 30;

    protected override void Read(SettingReader reader)
    {
        Secret = reader.RequiredString(SecretKey);
        Issuer = reader.String(IssuerKey, "frostaura-reel");
        Audience = reader.String(AudienceKey, "frostaura-reel-app");
        AccessMinutes = reader.Int(AccessMinutesKey, 15, min: 1, max: 1440);
        RefreshDays = reader.Int(RefreshDaysKey, 30, min: 1, max: 365);
    }
}
