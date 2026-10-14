using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MiningAssistant.Core.Licensing;

/// <summary>
/// Name-bound license keys: XXXX-XXXX-XXXX-XXXX (16 Crockford base-32 characters = 80 bits), computed as
/// HMAC-SHA256(secret, "MA1|" + normalized name). Only someone with the secret can make a valid key, and a
/// key only works with the name it was made for — which the app shows on screen ("Welcome …").
///
/// Limitation, by design: 16 characters cannot hold a public-key signature, so the app must contain the
/// same secret to check keys. This stops casual key sharing, not a determined reverse-engineer.
/// If the repository ever becomes public, rotate the secret (all keys must then be re-issued).
/// </summary>
public static class LicenseKey
{
    private const string Scheme = "MA1";
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford: no I, L, O, U

    // Project secret (generated 2026-10-14). Keep this repository private.
    private static readonly byte[] Secret = Convert.FromHexString("4EFEE4F63D610E096DB46BB88FBC204DAE3105E6BEA44019BB06BD904DA8B86C");

    /// <summary>"  vorxen   sc " → "VORXEN SC": case and extra spaces do not change the key.</summary>
    public static string NormalizeName(string name) => Regex.Replace(name.Trim(), @"\s+", " ").ToUpperInvariant();

    public static string Generate(string name)
    {
        var n = NormalizeName(name);
        if (n.Length == 0) throw new ArgumentException("Name is empty.", nameof(name));
        var mac = HMACSHA256.HashData(Secret, Encoding.UTF8.GetBytes($"{Scheme}|{n}"));
        return Format(ToBase32(mac)[..16]);
    }

    /// <summary>
    /// Cleans what a user typed: case, spaces, missing dashes, and the look-alikes O→0, I/L→1, U→V.
    /// Returns the canonical XXXX-XXXX-XXXX-XXXX form, or null if it cannot be a key.
    /// </summary>
    public static string? NormalizeKey(string key)
    {
        var sb = new StringBuilder(16);
        foreach (var raw in key.ToUpperInvariant())
        {
            var c = raw switch { 'O' => '0', 'I' or 'L' => '1', 'U' => 'V', _ => raw };
            if (Alphabet.Contains(c)) sb.Append(c);
            else if (!char.IsWhiteSpace(c) && c != '-') return null;
        }
        return sb.Length == 16 ? Format(sb.ToString()) : null;
    }

    public static bool IsValid(string name, string key)
    {
        if (string.IsNullOrWhiteSpace(name) || NormalizeKey(key) is not { } k) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(k), Encoding.ASCII.GetBytes(Generate(name)));
    }

    private static string Format(string s16) => $"{s16[..4]}-{s16[4..8]}-{s16[8..12]}-{s16[12..16]}";

    private static string ToBase32(byte[] data)
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

/// <summary>Activation stored in config/license.json.</summary>
public sealed class License
{
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    public DateTime ActivatedAt { get; set; }

    public bool IsValid => LicenseKey.IsValid(Name, Key);
}
