using System.Security.Cryptography;

namespace MiningAssistant.Core.Licensing;

/// <summary>
/// Vorxen's private keys (license + release signing) live in secrets/*.pem, encrypted with a password
/// (PKCS#8, PBES2: PBKDF2-SHA256 + AES-256-CBC). Only the CLI and the web keygen ever open them; the app
/// itself only contains the public halves.
/// </summary>
public static class KeyFiles
{
    public const int Iterations = 600_000;

    public static bool IsEncrypted(string pem) => pem.Contains("BEGIN ENCRYPTED PRIVATE KEY", StringComparison.Ordinal);

    /// <summary>Opens a private key; <paramref name="password"/> is asked only when the file is encrypted.</summary>
    public static ECDsa Open(string path, Func<string> password)
    {
        var pem = File.ReadAllText(path);
        var ec = ECDsa.Create();
        try
        {
            if (IsEncrypted(pem)) ec.ImportFromEncryptedPem(pem, password());
            else ec.ImportFromPem(pem);
            return ec;
        }
        catch (CryptographicException ex)
        {
            ec.Dispose();
            throw new CryptographicException($"Could not open {Path.GetFileName(path)} — wrong password?", ex);
        }
    }

    /// <summary>Encrypts a private key in place with <paramref name="password"/> (no-op if it already is).</summary>
    public static bool Protect(string path, string password)
    {
        var pem = File.ReadAllText(path);
        if (IsEncrypted(pem)) return false;
        using var ec = ECDsa.Create();
        ec.ImportFromPem(pem);
        var encrypted = ec.ExportEncryptedPkcs8PrivateKeyPem(password,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, Iterations));
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, encrypted);
        using (var check = ECDsa.Create()) check.ImportFromEncryptedPem(File.ReadAllText(tmp), password); // round-trip before replacing
        File.Move(tmp, path, overwrite: true);
        return true;
    }
}
