using System.Security.Cryptography;
using System.Text;
using MiningAssistant.Core.Licensing;
using MiningAssistant.Core.Updates;

namespace MiningAssistant.Tests;

public class UpdateTests
{
    private static UpdateManifest Sample() => new()
    {
        Version = "0.1.11", Asset = "MiningAssistant.zip", Size = 77_000_000,
        Sha256 = new string('a', 64), Notes = "test",
    };

    private static (ECDsa key, string pub) NewKey()
    {
        var (priv, pub) = UpdateVerifier.CreateKeyPair();
        var ec = ECDsa.Create();
        ec.ImportFromPem(priv);
        return (ec, pub);
    }

    private static UpdateManifest Copy(UpdateManifest m, Action<UpdateManifest> change)
    {
        var c = new UpdateManifest
        {
            Version = m.Version, Asset = m.Asset, Size = m.Size, Sha256 = m.Sha256, Notes = m.Notes,
            IssuedAt = m.IssuedAt, Signature = m.Signature, Signature2 = m.Signature2,
        };
        change(c);
        return c;
    }

    [Fact]
    public void Signed_manifest_verifies_and_any_change_breaks_it()
    {
        var (key, pub) = NewKey();
        using (key)
        {
            var m = Sample();
            UpdateVerifier.Sign(m, key);
            Assert.True(UpdateVerifier.IsSignatureValid(m, pub));

            Assert.False(UpdateVerifier.IsSignatureValid(Copy(m, x => x.Version = "9.9.9"), pub));
            Assert.False(UpdateVerifier.IsSignatureValid(Copy(m, x => x.Size = 1), pub));
            Assert.False(UpdateVerifier.IsSignatureValid(Copy(m, x => x.Sha256 = new string('b', 64)), pub));
            Assert.False(UpdateVerifier.IsSignatureValid(Copy(m, x => x.Asset = "evil.zip"), pub));
            Assert.False(UpdateVerifier.IsSignatureValid(Copy(m, x => x.IssuedAt = x.IssuedAt!.Value.AddDays(1)), pub)); // date is signed
            Assert.False(UpdateVerifier.IsSignatureValid(Copy(m, x => x.Signature2 = ""), pub));               // v1-only manifest: refused
            Assert.True(UpdateVerifier.IsSignatureValid(Copy(m, x => x.Notes = "edited"), pub));               // notes are not signed
        }
    }

    [Fact]
    public void Older_apps_can_still_verify_the_v1_signature()
    {
        // v0.1.9–v0.1.10 check Signature over "MA-UPDATE|version|asset|size|sha256" with the primary key.
        var (key, pub) = NewKey();
        using (key)
        {
            var m = Sample();
            UpdateVerifier.Sign(m, key);
            using var ec = ECDsa.Create();
            ec.ImportFromPem(pub);
            Assert.True(ec.VerifyData(Encoding.UTF8.GetBytes($"MA-UPDATE|{m.Version}|{m.Asset}|{m.Size}|{m.Sha256}"),
                Convert.FromBase64String(m.Signature), HashAlgorithmName.SHA256));
        }
    }

    [Fact]
    public void A_manifest_dated_in_the_future_is_refused()
    {
        var (key, pub) = NewKey();
        using (key)
        {
            var m = Sample();
            UpdateVerifier.Sign(m, key, DateTime.UtcNow.AddDays(30));
            Assert.False(UpdateVerifier.IsSignatureValid(m, pub));
        }
    }

    [Fact]
    public void The_app_trusts_the_primary_and_the_backup_release_keys_only()
    {
        Assert.Equal(2, UpdateVerifier.TrustedKeys.Length);
        var (stranger, _) = NewKey();
        using (stranger)
        {
            var m = Sample();
            UpdateVerifier.Sign(m, stranger);
            Assert.False(UpdateVerifier.IsSignatureValid(m)); // checked against the app's embedded keys
        }
    }

    [Fact]
    public void The_release_keys_on_this_pc_match_the_keys_built_into_the_app()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MiningAssistant.sln"))) dir = dir.Parent;
        foreach (var (file, pub) in new[] { ("update-signing-key.pem", UpdateVerifier.PublicKeyPem), ("update-backup-key.pem", UpdateVerifier.BackupPublicKeyPem) })
        {
            var path = Path.Combine(dir!.FullName, "secrets", file);
            if (!File.Exists(path) || KeyFiles.IsEncrypted(File.ReadAllText(path))) continue; // only checkable before it is password-protected
            using var key = KeyFiles.Open(path, () => "");
            var m = Sample();
            UpdateVerifier.Sign(m, key);
            Assert.True(UpdateVerifier.IsSignatureValid(m, pub), file);
        }
    }

    [Theory]
    [InlineData("0.1.9", "0.1.8", true)]
    [InlineData("0.1.10", "0.1.9", true)]
    [InlineData("v0.2.0", "0.1.9+abc123", true)]
    [InlineData("0.1.8", "0.1.8", false)]
    [InlineData("0.1.7", "0.1.8", false)]   // never downgrade
    [InlineData("garbage", "0.1.8", false)]
    public void Only_strictly_newer_versions_are_offered(string candidate, string current, bool newer) =>
        Assert.Equal(newer, UpdateVerifier.IsNewer(candidate, current));

    [Fact]
    public void Password_protected_release_keys_open_only_with_the_password()
    {
        var file = Path.Combine(Path.GetTempPath(), "ma-key-" + Guid.NewGuid().ToString("N") + ".pem");
        try
        {
            File.WriteAllText(file, UpdateVerifier.CreateKeyPair().privatePem);
            Assert.True(KeyFiles.Protect(file, "correct horse battery"));
            Assert.True(KeyFiles.IsEncrypted(File.ReadAllText(file)));
            Assert.False(KeyFiles.Protect(file, "again"));                       // already protected
            using (var ok = KeyFiles.Open(file, () => "correct horse battery")) Assert.NotNull(ok);
            Assert.ThrowsAny<CryptographicException>(() => KeyFiles.Open(file, () => "wrong"));
        }
        finally { File.Delete(file); }
    }
}