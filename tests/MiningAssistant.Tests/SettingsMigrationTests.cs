using MiningAssistant.Core.Settings;

namespace MiningAssistant.Tests;

public class SettingsMigrationTests
{
    private static AppSettings FromJson(string json) => MiningAssistant.Core.Json.Deserialize<AppSettings>(json);
    private static (string, string, string) Keys(AppSettings s) => (s.Hotkeys.ToggleOverlay, s.Hotkeys.MoveOverlay, s.Hotkeys.DebugSnapshot);

    [Fact]
    public void New_settings_use_page_up_page_down_and_backslash()
    {
        Assert.Equal(("PageUp", "PageDown", "\\"), Keys(new AppSettings()));
    }

    [Fact]
    public void Old_bracket_defaults_move_off_the_mission_keys()
    {
        // settings.json written by v0.1.4 or older: no configVersion, "[" overlay, "]" snapshot, "\" move
        var s = FromJson("""{"hotkeys":{"toggleOverlay":"[","debugSnapshot":"]","moveOverlay":"\\"}}""");
        Assert.True(s.Migrate());
        Assert.Equal(("PageUp", "PageDown", "\\"), Keys(s));
        Assert.Equal(AppSettings.CurrentConfigVersion, s.ConfigVersion);
        Assert.False(s.Migrate()); // only once
    }

    [Fact]
    public void V015_defaults_swap_move_and_snapshot()
    {
        // v0.1.5: overlay Page Up, snapshot Page Down, move "\"
        var s = FromJson("""{"configVersion":1,"hotkeys":{"toggleOverlay":"PageUp","debugSnapshot":"PageDown","moveOverlay":"\\"}}""");
        Assert.True(s.Migrate());
        Assert.Equal(("PageUp", "PageDown", "\\"), Keys(s));
        Assert.Equal("PageDown", s.Hotkeys.MoveOverlay);
        Assert.Equal("\\", s.Hotkeys.DebugSnapshot);
    }

    [Fact]
    public void Keys_the_user_chose_are_kept()
    {
        var s = FromJson("""{"hotkeys":{"toggleOverlay":"F9","debugSnapshot":"Ctrl+F10","moveOverlay":"F11"}}""");
        s.Migrate();
        Assert.Equal(("F9", "F11", "Ctrl+F10"), Keys(s));
    }

    [Fact]
    public void A_custom_move_key_is_never_doubled_up()
    {
        // old "]" snapshot but a custom move key: snapshot goes to Page Down, move stays custom
        var s = FromJson("""{"hotkeys":{"toggleOverlay":"[","debugSnapshot":"]","moveOverlay":"M"}}""");
        s.Migrate();
        Assert.Equal(("PageUp", "M", "PageDown"), Keys(s));
    }

    [Fact]
    public void After_migration_brackets_chosen_on_purpose_stay()
    {
        var s = new AppSettings { ConfigVersion = AppSettings.CurrentConfigVersion };
        s.Hotkeys.ToggleOverlay = "[";
        Assert.False(s.Migrate());
        Assert.Equal("[", s.Hotkeys.ToggleOverlay);
    }
}
