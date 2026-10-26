using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FrostAura.Reel.Application.Configuration;

namespace FrostAura.Reel.Infrastructure.Security;

/// <summary>
/// The certificate that encrypts the Data Protection key ring at rest (J-12, K-16).
///
/// <para><b>What the key ring guards.</b> Every Trakt access and refresh token, the VAPID private
/// key and the settings-unlock tokens are protected with keys that live in the
/// <c>DataProtectionKeys</c> table. Unencrypted, a database dump — the nightly one included — is
/// enough to decrypt all of them. With a certificate configured, every NEW key is written encrypted
/// to it, and a dump alone is useless.</para>
///
/// <para><b>Old keys keep working.</b> Keys written before the certificate existed are stored in the
/// clear and are still read as they are; keys encrypted to a previous certificate are read with the
/// certificates in <c>DATAPROTECTION_OLD_CERT_PATHS</c>. Rotate by adding the outgoing certificate
/// there and pointing <c>DATAPROTECTION_CERT_PATH</c> at the new one. Every certificate file is a
/// PKCS#12 bundle opened with <c>DATAPROTECTION_CERT_PASSWORD</c>.</para>
///
/// <para><b>Losing the certificate loses every token it protects</b> — the account must reconnect
/// Trakt. The custody runbook is in <c>docs/deployment.md#key-ring-certificate</c>.</para>
///
/// <para>Unset, the ring stays unencrypted (today's behaviour) and production logs a warning at
/// startup.</para>
/// </summary>
public sealed class KeyRingOptions : ValidatedOptions
{
    public const string CertPathKey = "DATAPROTECTION_CERT_PATH";
    public const string CertPasswordKey = "DATAPROTECTION_CERT_PASSWORD";
    public const string OldCertPathsKey = "DATAPROTECTION_OLD_CERT_PATHS";

    /// <summary>The certificate new keys are encrypted to, or null when none is configured.</summary>
    public X509Certificate2? Certificate { get; private set; }

    /// <summary>Earlier certificates, kept only to decrypt keys they encrypted.</summary>
    public IReadOnlyList<X509Certificate2> OldCertificates { get; private set; } = [];

    private readonly List<string> loadProblems = [];

    protected override void Read(SettingReader reader)
    {
        loadProblems.Clear();
        var password = reader.Raw(CertPasswordKey);
        var path = reader.Raw(CertPathKey);
        Certificate = string.IsNullOrWhiteSpace(path) ? null : Load(CertPathKey, path.Trim(), password, requirePrivateKey: true);

        var old = new List<X509Certificate2>();
        foreach (var oldPath in (reader.Raw(OldCertPathsKey) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Load(OldCertPathsKey, oldPath, password, requirePrivateKey: true) is { } certificate)
            {
                old.Add(certificate);
            }
        }

        OldCertificates = old;
    }

    protected override void Check(Action<string> problem)
    {
        foreach (var message in loadProblems)
        {
            problem(message);
        }
    }

    private X509Certificate2? Load(string key, string path, string? password, bool requirePrivateKey)
    {
        // Messages name the key and the failure, never the path's contents or the password.
        if (!File.Exists(path))
        {
            loadProblems.Add($"{key}: the certificate file does not exist.");
            return null;
        }

        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password);
            if (requirePrivateKey && !certificate.HasPrivateKey)
            {
                loadProblems.Add($"{key}: the certificate has no private key, so it can encrypt the key ring but never decrypt it.");
                return null;
            }

            return certificate;
        }
        catch (CryptographicException)
        {
            loadProblems.Add($"{key}: the file is not a PKCS#12 certificate {CertPasswordKey} opens.");
            return null;
        }
    }
}
