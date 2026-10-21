using System.Text.RegularExpressions;
using MiningAssistant.Core.Licensing;

namespace MiningAssistant.Tests;

public class LicenseKeyTests
{
    private const string Pc = "7KQ2-M9XA";
    private const string OtherPc = "3F8D-ZT1W";

    [Fact]
    public void Key_has_16_characters_in_four_groups()
    {
        var key = LicenseKey.Generate("Vorxen", Pc);
        Assert.Matches(new Regex("^[0-9A-HJKMNP-TV-Z]{4}(-[0-9A-HJKMNP-TV-Z]{4}){3}$"), key);
        Assert.Equal(16, key.Replace("-", "").Length);
    }

    [Fact]
    public void Same_name_and_pc_same_key_and_different_names_differ()
    {
        Assert.Equal(LicenseKey.Generate("Vorxen", Pc), LicenseKey.Generate("Vorxen", Pc));
        Assert.NotEqual(LicenseKey.Generate("Vorxen", Pc), LicenseKey.Generate("Vorxen2", Pc));
        Assert.NotEqual(LicenseKey.Generate("Ana", Pc), LicenseKey.Generate("Bob", Pc));
    }

    [Fact]
    public void Key_only_works_on_the_pc_it_was_made_for()
    {
        var key = LicenseKey.Generate("Vorxen", Pc);
        Assert.True(LicenseKey.IsValid("Vorxen", key, Pc));
        Assert.False(LicenseKey.IsValid("Vorxen", key, OtherPc));     // copied folder / shared key on another PC
        Assert.NotEqual(key, LicenseKey.Generate("Vorxen", OtherPc));
        Assert.False(new License { Name = "Vorxen", Key = key, PcCode = Pc }.IsValidOn(OtherPc));
        Assert.True(new License { Name = "Vorxen", Key = key, PcCode = OtherPc }.IsValidOn(Pc)); // stored code is not trusted
    }

    [Fact]
    public void Old_name_only_keys_are_rejected()
    {
        // v0.1.1–v0.1.2 key for "Vorxen" (scheme MA1, no PC)
        Assert.False(LicenseKey.IsValid("Vorxen", "44SD-V51S-S737-M2QD", Pc));
    }

    [Fact]
    public void Pc_code_is_stable_short_and_does_not_reveal_the_machine_id()
    {
        const string guid = "a1b2c3d4-0000-4e5f-9a8b-123456789abc";
        var code = PcCode.FromMachineId(guid);
        Assert.Matches(new Regex("^[0-9A-HJKMNP-TV-Z]{4}-[0-9A-HJKMNP-TV-Z]{4}$"), code);
        Assert.Equal(code, PcCode.FromMachineId(guid.ToUpperInvariant()));
        Assert.Equal(code, PcCode.FromMachineId(" " + guid + " "));
        Assert.NotEqual(code, PcCode.FromMachineId("a1b2c3d4-0000-4e5f-9a8b-123456789abd"));
        Assert.DoesNotContain("A1B2", code.Replace("-", ""));
    }

    [Fact]
    public void Pc_code_typed_by_hand_is_forgiving()
    {
        Assert.Equal(Pc, PcCode.Normalize("7kq2m9xa"));
        Assert.Equal(Pc, PcCode.Normalize(" 7KQ2 - M9XA "));
        Assert.Null(PcCode.Normalize("7KQ2-M9X"));
        Assert.Null(PcCode.Normalize("hello world"));
        Assert.Equal(LicenseKey.Generate("Vorxen", Pc), LicenseKey.Generate("Vorxen", "7kq2-m9xa"));
    }

    [Fact]
    public void Name_case_and_spaces_do_not_matter()
    {
        var key = LicenseKey.Generate("Joao Silva", Pc);
        Assert.True(LicenseKey.IsValid("  joao   SILVA ", key, Pc));
    }

    [Fact]
    public void Typed_key_is_forgiving()
    {
        var key = LicenseKey.Generate("Vorxen", Pc);
        Assert.True(LicenseKey.IsValid("Vorxen", key.ToLowerInvariant(), Pc));
        Assert.True(LicenseKey.IsValid("Vorxen", key.Replace("-", ""), Pc));
        Assert.True(LicenseKey.IsValid("Vorxen", " " + key.Replace("-", " ") + " ", Pc));
        // look-alikes typed by mistake
        Assert.True(LicenseKey.IsValid("Vorxen", key.Replace('0', 'O').Replace('1', 'I'), Pc));
    }

    [Fact]
    public void Wrong_name_or_altered_key_is_rejected()
    {
        var key = LicenseKey.Generate("Vorxen", Pc);
        Assert.False(LicenseKey.IsValid("Someone Else", key, Pc));
        var chars = key.ToCharArray();
        chars[0] = chars[0] == '2' ? '3' : '2';
        Assert.False(LicenseKey.IsValid("Vorxen", new string(chars), Pc));
        Assert.False(LicenseKey.IsValid("Vorxen", key[..^1], Pc));
        Assert.False(LicenseKey.IsValid("", key, Pc));
        Assert.False(LicenseKey.IsValid("Vorxen", "not a key at all!", Pc));
        Assert.False(LicenseKey.IsValid("Vorxen", key, "not a pc"));
    }
}
