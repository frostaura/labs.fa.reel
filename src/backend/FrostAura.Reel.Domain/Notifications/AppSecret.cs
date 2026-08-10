namespace FrostAura.Reel.Domain.Notifications;

/// <summary>
/// App-global key/value secret store (first tenant: the VAPID key pair). Deliberately NOT
/// account-scoped — these are application credentials, not user data. Values that need
/// confidentiality are wrapped via ISecretProtector before landing in
/// <see cref="ValueProtected"/>; genuinely public values (e.g. the VAPID public key) may be
/// stored plain.
/// </summary>
public class AppSecret
{
    /// <summary>Natural key, e.g. "vapid:public" / "vapid:private".</summary>
    public string Key { get; set; } = string.Empty;

    public string ValueProtected { get; set; } = string.Empty;
}
