using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace MiningAssistant.Core.Updates;

/// <summary>
/// update.json, published next to MiningAssistant.zip in every release of the downloads repository.
/// It says which version the zip is, its exact size and SHA-256, when it was issued, and carries ECDSA
/// P-256 signatures made with Vorxen's release key (secrets/update-signing-key.pem, password-protected,
/// never in git). The app installs a download only when a signature checks out against one of
/// <see cref="UpdateVerifier.TrustedKeys"/> and the zip matches the size and hash — so a compromised GitHub
/// account alone cannot push code to players.
/// </summary>
public sealed class UpdateManifest
{
    public string Version { get; set; } = "";
    public string Asset { get; set; } = "MiningAssistant.zip";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    /// <summary>When the release was signed (UTC). Covered by <see cref="Signature2"/>; used against replays.</summary>
    public DateTime? IssuedAt { get; set; }
    public string Notes { get; set; } = "";
    /// <summary>v1 signature (read by v0.1.9–v0.1.10): version, asset, size, hash.</summary>
    public string Signature { get; set; } = "";
    /// <summary>v2 signature (v0.1.11+): also covers <see cref="IssuedAt"/>; may be made with the backup key.</summary>
    public string Signature2 { get; set; } = "";

    [JsonIgnore]
    public string SignedText => $"MA-UPDATE|{Version}|{Asset}|{Size}|{Sha256?.ToLowerInvariant()}";

    [JsonIgnore]
    public string SignedText2 =>
        $"MA-UPDATE2|{Version}|{Asset}|{Size}|{Sha256?.ToLowerInvariant()}|{IssuedAt?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}";
}

public static class UpdateVerifier
{
    /// <summary>Primary release key (signs every release).</summary>
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAELtEXSGbNkWyonC7IGRRmR3LiCG8K
        q967rgirecYc3LzfVD6OmMMtgqp9JfBg4p2+ELuLMdvPmXYVhDx9CxegIA==
        -----END PUBLIC KEY-----
        """;

    /// <summary>
    /// Backup release key (secrets/update-backup-key.pem, kept offline). If the primary key ever leaks,
    /// releases are signed with this one and the leaked key is removed in that same update.
    /// </summary>
    public const string BackupPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE03V8IW8DsRAUUUVu+BIbs2dinNb+
        qJApGvS3j69UMcfxYC0v+ub4UuP+V9azneXcmluKCLY7NTq5+RHA3wNx5Q==
        -----END PUBLIC KEY-----
        """;

    public static readonly string[] TrustedKeys = { PublicKeyPem, BackupPublicKeyPem };

    /// <summary>Accepts the manifest when its v2 signature (with the issue date) verifies against a trusted key.</summary>
    public static bool IsSignatureValid(UpdateManifest m) => IsSignatureValid(m, TrustedKeys);

    public static bool IsSignatureValid(UpdateManifest m, params string[] publicKeyPems)
    {
        if (m is null || !IsWellFormed(m) || m.IssuedAt is null || string.IsNullOrWhiteSpace(m.Signature2)) return false;
        if (m.IssuedAt.Value.ToUniversalTime() > DateTime.UtcNow.AddDays(2)) return false; // from the future: refuse
        return publicKeyPems.Any(pem => Verify(pem, m.SignedText2, m.Signature2));
    }

    private static bool Verify(string publicKeyPem, string text, string signature)
    {
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportFromPem(publicKeyPem);
            return ec.VerifyData(Encoding.UTF8.GetBytes(text), Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Shape checks on top of the signature (defence in depth: these values end up in a download URL and file names).</summary>
    public static bool IsWellFormed(UpdateManifest m) =>
        m.Version is not null && System.Version.TryParse(Strip(m.Version), out _)
        && m.Asset is not null && Regex.IsMatch(m.Asset, @"^[A-Za-z0-9._-]{1,100}$") && !m.Asset.Contains("..")
        && m.Sha256 is not null && Regex.IsMatch(m.Sha256, "^[0-9a-fA-F]{64}$")
        && m.Size is > 0 and < 1_000_000_000;

    /// <summary>"owner/name" of a GitHub repository; anything else is refused before it reaches a URL.</summary>
    public static bool IsRepositoryName(string? r) =>
        r is not null && Regex.IsMatch(r, @"^[A-Za-z0-9_.-]{1,100}/[A-Za-z0-9_.-]{1,100}$");

    /// <summary>Release side (mining-cli sign-update): stamps IssuedAt and fills both signatures.</summary>
    public static void Sign(UpdateManifest m, ECDsa key, DateTime? issuedAtUtc = null)
    {
        var at = (issuedAtUtc ?? DateTime.UtcNow).ToUniversalTime();
        m.IssuedAt = new DateTime(at.Year, at.Month, at.Day, at.Hour, at.Minute, at.Second, DateTimeKind.Utc);
        m.Signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(m.SignedText), HashAlgorithmName.SHA256));
        m.Signature2 = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(m.SignedText2), HashAlgorithmName.SHA256));
    }

    /// <summary>New key pair: (private PKCS#8 PEM, public SubjectPublicKeyInfo PEM).</summary>
    public static (string privatePem, string publicPem) CreateKeyPair()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ec.ExportPkcs8PrivateKeyPem(), ec.ExportSubjectPublicKeyInfoPem());
    }

    public static string Sha256Hex(Stream s) => Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();

    /// <summary>"0.1.10" &gt; "0.1.9"; anything unparsable is never newer.</summary>
    public static bool IsNewer(string candidate, string current) =>
        System.Version.TryParse(Strip(candidate), out var c) && System.Version.TryParse(Strip(current), out var cur) && c > cur;

    /// <summary>"v0.1.9+abc" → "0.1.9".</summary>
    public static string Strip(string v)
    {
        v = (v ?? "").Trim().TrimStart('v', 'V');
        var plus = v.IndexOfAny(new[] { '+', '-', ' ' });
        return plus >= 0 ? v[..plus] : v;
    }
}
