using MiningAssistant.Core.Settings;

namespace MiningAssistant.Tests;

public class SettingsMigrationTests
{
    [Fact]
    public void New_settings_use_page_up_and_page_down()
    {
        var s = new AppSettings();
        Assert.Equal(("PageUp", "PageDown", "\\"), (s.Hotkeys.ToggleOverlay, s.Hotkeys.DebugSnapshot, s.Hotkeys.MoveOverlay));
    }

    [Fact]
    public void Old_bracket_defaults_move_off_the_mission_keys()
    {
        // settings.json written by v0.1.4 or older: no configVersion, "[" and "]"
        var s = MiningAssistant.Core.Json.Deserialize<AppSettings>("""{"hotkeys":{"toggleOverlay":"[","debugSnapshot":"]","moveOverlay":"\\"}}""");
        Assert.True(s.Migrate());
        Assert.Equal(("PageUp", "PageDown", "\\"), (s.Hotkeys.ToggleOverlay, s.Hotkeys.DebugSnapshot, s.Hotkeys.MoveOverlay));
        Assert.Equal(AppSettings.CurrentConfigVersion, s.ConfigVersion);
        Assert.False(s.Migrate()); // only once
    }

    [Fact]
    public void Keys_the_user_chose_are_kept()
    {
        var s = MiningAssistant.Core.Json.Deserialize<AppSettings>("""{"hotkeys":{"toggleOverlay":"F9","debugSnapshot":"Ctrl+F10"}}""");
        s.Migrate();
        Assert.Equal(("F9", "Ctrl+F10"), (s.Hotkeys.ToggleOverlay, s.Hotkeys.DebugSnapshot));
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
