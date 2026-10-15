using MiningAssistant.Core.Updates;

namespace MiningAssistant.Tests;

public class UpdateTests
{
    private static UpdateManifest Sample() => new()
    {
        Version = "0.1.9", Asset = "MiningAssistant.zip", Size = 77_000_000,
        Sha256 = new string('a', 64), Notes = "test",
    };

    [Fact]
    public void Signed_manifest_verifies_and_any_change_breaks_it()
    {
        var (priv, pub) = UpdateVerifier.CreateKeyPair();
        var m = Sample();
        UpdateVerifier.Sign(m, priv);
        Assert.True(UpdateVerifier.IsSignatureValid(m, pub));

        Assert.False(UpdateVerifier.IsSignatureValid(Copy(m, x => x.Version = "9.9.9"), pub));
        Assert.False(UpdateVerifier.IsSignatureValid(Copy(m, x => x.Size = 1), pub));
        Assert.False(UpdateVerifier.IsSignatureValid(Copy(m, x => x.Sha256 = new string('b', 64)), pub));
        Assert.False(UpdateVerifier.IsSignatureValid(Copy(m, x => x.Asset = "evil.zip"), pub));
        Assert.True(UpdateVerifier.IsSignatureValid(Copy(m, x => x.Notes = "edited"), pub)); // notes are not signed
    }

    [Fact]
    public void A_signature_from_another_key_is_rejected()
    {
        var (otherPriv, _) = UpdateVerifier.CreateKeyPair();
        var m = Sample();
        UpdateVerifier.Sign(m, otherPriv);
        Assert.False(UpdateVerifier.IsSignatureValid(m)); // checked against the app's embedded release key
        Assert.False(UpdateVerifier.IsSignatureValid(Copy(m, x => x.Signature = "not base64!")));
        Assert.False(UpdateVerifier.IsSignatureValid(Copy(m, x => x.Signature = "")));
    }

    [Fact]
    public void The_release_key_on_this_pc_matches_the_key_built_into_the_app()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MiningAssistant.sln"))) dir = dir.Parent;
        var key = Path.Combine(dir!.FullName, "secrets", "update-signing-key.pem");
        if (!File.Exists(key)) return; // only the release PC has it
        var m = Sample();
        UpdateVerifier.Sign(m, File.ReadAllText(key));
        Assert.True(UpdateVerifier.IsSignatureValid(m));
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

    private static UpdateManifest Copy(UpdateManifest m, Action<UpdateManifest> change)
    {
        var c = new UpdateManifest { Version = m.Version, Asset = m.Asset, Size = m.Size, Sha256 = m.Sha256, Notes = m.Notes, Signature = m.Signature };
        change(c);
        return c;
    }
}
