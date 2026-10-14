using System.Text.RegularExpressions;
using MiningAssistant.Core.Licensing;

namespace MiningAssistant.Tests;

public class LicenseKeyTests
{
    [Fact]
    public void Key_has_16_characters_in_four_groups()
    {
        var key = LicenseKey.Generate("Vorxen");
        Assert.Matches(new Regex("^[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){3}$"), key);
        Assert.Equal(16, key.Replace("-", "").Length);
    }

    [Fact]
    public void Same_name_same_key_and_different_names_differ()
    {
        Assert.Equal(LicenseKey.Generate("Vorxen"), LicenseKey.Generate("Vorxen"));
        Assert.NotEqual(LicenseKey.Generate("Vorxen"), LicenseKey.Generate("Vorxen2"));
        Assert.NotEqual(LicenseKey.Generate("Ana"), LicenseKey.Generate("Bob"));
    }

    [Fact]
    public void Name_case_and_spaces_do_not_matter()
    {
        var key = LicenseKey.Generate("Joao Silva");
        Assert.True(LicenseKey.IsValid("  joao   SILVA ", key));
    }

    [Fact]
    public void Typed_key_is_forgiving()
    {
        var key = LicenseKey.Generate("Vorxen");
        Assert.True(LicenseKey.IsValid("Vorxen", key.ToLowerInvariant()));
        Assert.True(LicenseKey.IsValid("Vorxen", key.Replace("-", "")));
        Assert.True(LicenseKey.IsValid("Vorxen", " " + key.Replace("-", " ") + " "));
        // look-alikes typed by mistake
        Assert.True(LicenseKey.IsValid("Vorxen", key.Replace('0', 'O').Replace('1', 'I')));
    }

    [Fact]
    public void Wrong_name_or_altered_key_is_rejected()
    {
        var key = LicenseKey.Generate("Vorxen");
        Assert.False(LicenseKey.IsValid("Someone Else", key));
        var chars = key.ToCharArray();
        chars[0] = chars[0] == '2' ? '3' : '2';
        Assert.False(LicenseKey.IsValid("Vorxen", new string(chars)));
        Assert.False(LicenseKey.IsValid("Vorxen", key[..^1]));
        Assert.False(LicenseKey.IsValid("", key));
        Assert.False(LicenseKey.IsValid("Vorxen", "not a key at all!"));
    }
}
