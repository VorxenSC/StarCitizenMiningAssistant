using System.Security.Cryptography;
using System.Text;

namespace MiningAssistant.Core.Updates;

/// <summary>
/// update.json, published next to MiningAssistant.zip in every release of the downloads repository.
/// It says which version the zip is, its exact size and SHA-256, and carries an ECDSA P-256 signature
/// over those values made with the release key (secrets/update-signing-key.pem, never in git).
/// The app installs a download only when the signature checks out against <see cref="UpdateVerifier.PublicKeyPem"/>
/// and the zip matches the size and hash — so a compromised GitHub account alone cannot push code to players.
/// </summary>
public sealed class UpdateManifest
{
    public string Version { get; set; } = "";
    public string Asset { get; set; } = "MiningAssistant.zip";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    public string Notes { get; set; } = "";
    public string Signature { get; set; } = "";

    /// <summary>Exactly what is signed (notes are informational and not covered).</summary>
    public string SignedText => $"MA-UPDATE|{Version}|{Asset}|{Size}|{Sha256.ToLowerInvariant()}";
}

public static class UpdateVerifier
{
    /// <summary>Public half of the release key. The private half never leaves the build PC.</summary>
    public const string PublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAELtEXSGbNkWyonC7IGRRmR3LiCG8K
        q967rgirecYc3LzfVD6OmMMtgqp9JfBg4p2+ELuLMdvPmXYVhDx9CxegIA==
        -----END PUBLIC KEY-----
        """;

    public static bool IsSignatureValid(UpdateManifest m, string publicKeyPem = PublicKeyPem)
    {
        if (string.IsNullOrWhiteSpace(m.Signature) || string.IsNullOrWhiteSpace(m.Version) || m.Size <= 0 || m.Sha256.Length != 64) return false;
        try
        {
            using var ec = ECDsa.Create();
            ec.ImportFromPem(publicKeyPem);
            return ec.VerifyData(Encoding.UTF8.GetBytes(m.SignedText), Convert.FromBase64String(m.Signature), HashAlgorithmName.SHA256);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Release side (mining-cli sign-update): fills Signature.</summary>
    public static void Sign(UpdateManifest m, string privateKeyPem)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(privateKeyPem);
        m.Signature = Convert.ToBase64String(ec.SignData(Encoding.UTF8.GetBytes(m.SignedText), HashAlgorithmName.SHA256));
    }

    /// <summary>New key pair: (private PKCS#8 PEM, public SubjectPublicKeyInfo PEM).</summary>
    public static (string privatePem, string publicPem) CreateKeyPair()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ec.ExportPkcs8PrivateKeyPem(), ec.ExportSubjectPublicKeyInfoPem());
    }

    public static string PublicPemOf(string privateKeyPem)
    {
        using var ec = ECDsa.Create();
        ec.ImportFromPem(privateKeyPem);
        return ec.ExportSubjectPublicKeyInfoPem();
    }

    public static string Sha256Hex(Stream s) => Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();

    /// <summary>"0.1.10" &gt; "0.1.9"; anything unparsable is never newer.</summary>
    public static bool IsNewer(string candidate, string current) =>
        System.Version.TryParse(Strip(candidate), out var c) && System.Version.TryParse(Strip(current), out var cur) && c > cur;

    /// <summary>"v0.1.9+abc" → "0.1.9".</summary>
    public static string Strip(string v)
    {
        v = v.Trim().TrimStart('v', 'V');
        var plus = v.IndexOfAny(new[] { '+', '-', ' ' });
        return plus >= 0 ? v[..plus] : v;
    }
}
