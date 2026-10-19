using System.Security.Cryptography;
using System.Text.RegularExpressions;
using MiningAssistant.Core.Licensing;
using MiningAssistant.Core.Updates;

namespace MiningAssistant.Tests;

/// <summary>MA3 keys: signed with a private key, checked by the app with the public half only.</summary>
public sealed class LicenseKeyTests : IDisposable
{
    private const string Pc = "7KQ2-M9XA";
    private const string OtherPc = "3F8D-ZT1W";

    // A throw-away key pair: the tests never need Vorxen's real private key.
    private readonly ECDsa _key;
    private readonly string _pub;

    public LicenseKeyTests()
    {
        var (priv, pub) = UpdateVerifier.CreateKeyPair();
        _key = ECDsa.Create();
        _key.ImportFromPem(priv);
        _pub = pub;
    }

    public void Dispose() => _key.Dispose();

    private string Make(string name, string pc = Pc) => LicenseKey.Generate(name, pc, _key);
    private bool Valid(string name, string key, string pc = Pc) => LicenseKey.IsValid(name, key, pc, _pub);

    [Fact]
    public void Key_is_103_crockford_characters_in_groups_of_8()
    {
        var key = Make("Vorxen");
        Assert.Matches(new Regex("^[0-9A-HJKMNP-TV-Z]{8}(-[0-9A-HJKMNP-TV-Z]{1,8}){12}$"), key);
        Assert.Equal(LicenseKey.KeyLength, key.Replace("-", "").Length);
    }

    [Fact]
    public void Key_only_works_for_its_name_and_its_pc()
    {
        var key = Make("Vorxen");
        Assert.True(Valid("Vorxen", key));
        Assert.False(Valid("Vorxen", key, OtherPc));       // copied folder / shared key on another PC
        Assert.False(Valid("Someone Else", key));
    }

    [Fact]
    public void The_app_rejects_keys_signed_by_anyone_else()
    {
        // Signed with a key that is not Vorxen's: the app's built-in public key must refuse it.
        Assert.False(LicenseKey.IsValid("Vorxen", Make("Vorxen"), Pc));
        Assert.False(new License { Name = "Vorxen", Key = Make("Vorxen"), PcCode = Pc }.IsValidOn(Pc));
    }

    [Fact]
    public void Old_short_keys_are_rejected()
    {
        Assert.False(Valid("Vorxen", "44SD-V51S-S737-M2QD"));   // MA1
        Assert.False(Valid("Vorxen", "HFFQ-MDQX-QR9B-W3G1"));   // MA2 (HMAC)
    }

    [Fact]
    public void Pasted_key_is_forgiving()
    {
        var key = Make("Vorxen");
        var raw = key.Replace("-", "");
        Assert.True(Valid("Vorxen", key.ToLowerInvariant()));
        Assert.True(Valid("Vorxen", raw));
        var wrapped = string.Join("\r\n", Enumerable.Range(0, 13).Select(i => raw.Substring(i * 8, Math.Min(8, raw.Length - i * 8))));
        Assert.True(Valid("Vorxen", "  " + wrapped + " "));    // pasted from a chat that wrapped the lines
        Assert.True(Valid("Vorxen", key.Replace('0', 'O').Replace('1', 'I')));
    }

    [Fact]
    public void Altered_or_truncated_keys_are_rejected()
    {
        var key = Make("Vorxen");
        var chars = key.ToCharArray();
        chars[10] = chars[10] == '2' ? '3' : '2';
        Assert.False(Valid("Vorxen", new string(chars)));
        Assert.False(Valid("Vorxen", key[..^1]));
        Assert.False(Valid("", key));
        Assert.False(Valid("Vorxen", "not a key at all!"));
        Assert.False(Valid("Vorxen", key, "not a pc"));
    }

    [Fact]
    public void Name_case_spaces_and_unicode_form_do_not_matter()
    {
        var key = Make("João Silva");
        Assert.True(Valid("  joão   SILVA ", key));
        Assert.True(Valid("João Silva", key)); // "ã" typed as a + combining tilde
    }

    [Fact]
    public void Pc_code_is_stable_short_and_forgiving()
    {
        const string guid = "a1b2c3d4-0000-4e5f-9a8b-123456789abc";
        var code = PcCode.FromMachineId(guid);
        Assert.Matches(new Regex("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$"), code);
        Assert.Equal(code, PcCode.FromMachineId(" " + guid.ToUpperInvariant() + " "));
        Assert.NotEqual(code, PcCode.FromMachineId("a1b2c3d4-0000-4e5f-9a8b-123456789abd"));
        Assert.Equal(Pc, PcCode.Normalize("7kq2m9xa"));
        Assert.Null(PcCode.Normalize("7KQ2-M9X"));
    }

    [Fact]
    public void The_license_key_on_this_pc_matches_the_key_built_into_the_app()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MiningAssistant.sln"))) dir = dir.Parent;
        var file = Path.Combine(dir!.FullName, "secrets", "license-signing-key.pem");
        if (!File.Exists(file) || KeyFiles.IsEncrypted(File.ReadAllText(file))) return; // only checkable before it is password-protected
        using var real = KeyFiles.Open(file, () => "");
        Assert.True(LicenseKey.IsValid("Vorxen", LicenseKey.Generate("Vorxen", Pc, real), Pc));
    }

    [Fact]
    public void Password_protected_keys_open_only_with_the_password()
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
