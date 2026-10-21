using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MiningAssistant.Core.Licensing;

/// <summary>
/// License keys bound to a name AND a PC: XXXX-XXXX-XXXX-XXXX (16 Crockford base-32 characters = 80 bits),
/// computed as HMAC-SHA256(secret, "MA2|" + normalized name + "|" + PC code). Only someone with the secret
/// can make a valid key; a key only works with the name it was made for (shown as "Welcome …") on the PC
/// whose code it was made for — so a copied app folder, or a key passed to a friend, does not activate.
///
/// The PC code (XXXX-XXXX) is a hash of the Windows installation id (see <see cref="PcCode"/>); the player
/// reads it in the activation window and sends it with their name to get a key.
///
/// The secret is not in the source: it lives in secrets/license-secret.txt (git-ignored) and is embedded
/// into this assembly at build time (see MiningAssistant.Core.csproj). The web keygen asks for it.
///
/// Limitation, by design: 16 characters cannot hold a public-key signature, so the app must contain the
/// same secret to check keys. This stops casual sharing, not a determined reverse-engineer who decompiles
/// the exe. Rotating the secret invalidates every key already issued.
/// </summary>
public static class LicenseKey
{
    private const string Scheme = "MA2"; // MA1 = name only (v0.1.1–v0.1.2), no longer accepted
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford: no I, L, O, U

    // Rotated 2026-10-15 (the previous one had been committed to git). Embedded at build time.
    private static readonly Lazy<byte[]> SecretBytes = new(LoadSecret);

    private static byte[] LoadSecret()
    {
        using var stream = typeof(LicenseKey).Assembly.GetManifestResourceStream("MiningAssistant.LicenseSecret")
            ?? throw new InvalidOperationException("License secret was not embedded at build time.");
        var hex = new StreamReader(stream).ReadToEnd().Trim();
        var bytes = Convert.FromHexString(hex);
        return bytes.Length == 32 ? bytes : throw new InvalidOperationException("License secret must be 32 bytes (64 hex characters).");
    }

    /// <summary>"  vorxen   sc " → "VORXEN SC": case and extra spaces do not change the key.</summary>
    public static string NormalizeName(string name) => Regex.Replace(name.Trim(), @"\s+", " ").ToUpperInvariant();

    public static string Generate(string name, string pcCode)
    {
        var n = NormalizeName(name);
        if (n.Length == 0) throw new ArgumentException("Name is empty.", nameof(name));
        var pc = PcCode.Normalize(pcCode) ?? throw new ArgumentException("PC code must be XXXX-XXXX.", nameof(pcCode));
        var mac = HMACSHA256.HashData(SecretBytes.Value, Encoding.UTF8.GetBytes($"{Scheme}|{n}|{pc}"));
        return Format(ToBase32(mac)[..16]);
    }

    /// <summary>
    /// Cleans what a user typed: case, spaces, missing dashes, and the look-alikes O→0, I/L→1, U→V.
    /// Returns the canonical XXXX-XXXX-XXXX-XXXX form, or null if it cannot be a key.
    /// </summary>
    public static string? NormalizeKey(string key) => Clean(key, 16) is { } s ? Format(s) : null;

    public static bool IsValid(string name, string key, string pcCode)
    {
        if (string.IsNullOrWhiteSpace(name) || NormalizeKey(key) is not { } k || PcCode.Normalize(pcCode) is null) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(k), Encoding.ASCII.GetBytes(Generate(name, pcCode)));
    }

    /// <summary>Upper-cases, maps look-alikes (O→0, I/L→1, U→V), drops spaces and dashes; null unless exactly <paramref name="length"/> characters remain.</summary>
    internal static string? Clean(string text, int length)
    {
        var sb = new StringBuilder(length);
        foreach (var raw in text.ToUpperInvariant())
        {
            var c = raw switch { 'O' => '0', 'I' or 'L' => '1', 'U' => 'V', _ => raw };
            if (Alphabet.Contains(c)) sb.Append(c);
            else if (!char.IsWhiteSpace(c) && c != '-') return null;
        }
        return sb.Length == length ? sb.ToString() : null;
    }

    private static string Format(string s16) => $"{s16[..4]}-{s16[4..8]}-{s16[8..12]}-{s16[12..16]}";

    internal static string ToBase32(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Short code that identifies this PC: XXXX-XXXX = first 40 bits of SHA-256("MA-PC|" + machine id) in
/// Crockford base 32. The machine id itself (Windows MachineGuid) never leaves the PC and cannot be
/// recovered from the code. It changes when Windows is reinstalled — the player then needs a new key.
/// </summary>
public static class PcCode
{
    public static string FromMachineId(string machineId)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("MA-PC|" + machineId.Trim().ToUpperInvariant()));
        var s = LicenseKey.ToBase32(hash)[..8];
        return $"{s[..4]}-{s[4..]}";
    }

    /// <summary>Forgiving like keys: case, spaces, missing dash and look-alikes. Null if not a PC code.</summary>
    public static string? Normalize(string code) => LicenseKey.Clean(code, 8) is { } s ? $"{s[..4]}-{s[4..]}" : null;
}

/// <summary>Activation stored in config/license.json. Valid only on the PC whose code the key was made for.</summary>
public sealed class License
{
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    /// <summary>PC code at activation time (informational; validity is always checked against the current PC).</summary>
    public string PcCode { get; set; } = "";
    public DateTime ActivatedAt { get; set; }

    public bool IsValidOn(string pcCode) => LicenseKey.IsValid(Name, Key, pcCode);
}
