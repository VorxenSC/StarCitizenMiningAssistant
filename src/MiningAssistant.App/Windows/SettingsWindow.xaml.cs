using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MiningAssistant.App.Services;
using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Materials;
using MiningAssistant.Core.Settings;
using MiningAssistant.Vision.Capture;

namespace MiningAssistant.App.Windows;

/// <summary>
/// Control panel: on/off switches for every function, Reload / Restart, a live view of what the
/// scanner captures and reads, and a health checklist that says what is wrong when nothing happens.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly Brush Ok = Frozen(0x4A, 0xDE, 0x80), Bad = Frozen(0xF8, 0x71, 0x71), Warn = Frozen(0xFA, 0xCC, 0x15);
    private static readonly Brush OkBar = Frozen(0x14, 0x53, 0x2D), BadBar = Frozen(0x7F, 0x1D, 0x1D), WarnBar = Frozen(0x71, 0x3F, 0x12);

    private static readonly Brush StartGreen = Frozen(0x16, 0xA3, 0x4A), StopButton = Frozen(0xB9, 0x1C, 0x1C), WarnButton = Frozen(0xB4, 0x53, 0x09);

    private readonly AppHost _host;
    private bool _loading;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        VersionText.Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?");
        Closing += (_, e) => { e.Cancel = true; Hide(); };
        Load(host.Settings);
    }

    /// <summary>Fills every control from the settings object.</summary>
    public void Load(AppSettings s)
    {
        _loading = true;

        LocateSwitch.IsChecked = s.Ocr.AutoLocate;
        HotkeySwitch.IsChecked = s.HotkeysEnabled;
        SoundSwitch.IsChecked = s.SoundAlerts;
        TargetSwitch.IsChecked = s.TargetOnly;
        MissedSwitch.IsChecked = s.SaveMissedReads;
        DebugSwitch.IsChecked = s.DebugMode;
        CaptureSwitch.IsChecked = s.Overlay.HideFromCapture;
        StartupSwitch.IsChecked = s.ShowPanelOnStartup;
        AutoStartSwitch.IsChecked = s.AutoStart;
        MinimizeSwitch.IsChecked = s.MinimizePanelOnStart;
        AlertRsBox.Text = RsAlert.Format(s.AlertRsValues);
        AlertMultiplesBox.IsChecked = s.AlertRsMultiples;
        UpdateAlertHint(s);
        OverlaySwitch.IsChecked = _host.IsOverlayVisible;

        VersionBox.ItemsSource = MaterialDatabaseLoader.ListVersions(_host.Paths.DataRoot);
        VersionBox.SelectedItem = s.GameVersion;
        if (VersionBox.SelectedIndex < 0) VersionBox.SelectedIndex = 0;

        MonitorBox.ItemsSource = Monitors.GetAll();
        MonitorBox.SelectedIndex = Math.Clamp(s.MonitorIndex, 0, Math.Max(0, MonitorBox.Items.Count - 1));

        Select(ContextBox, s.Matcher.Context.ToString());
        Select(IntervalBox, s.OcrIntervalMs.ToString(CultureInfo.InvariantCulture));
        ToleranceBox.Text = s.Matcher.MaxAbsoluteError.ToString(CultureInfo.InvariantCulture);
        VoteMinBox.Text = s.VoteMinAgreement.ToString(CultureInfo.InvariantCulture);
        VoteWindowBox.Text = s.VoteWindow.ToString(CultureInfo.InvariantCulture);
        TargetsBox.Text = string.Join(", ", s.TargetMaterials);
        SkipBox.IsChecked = s.SkipUnchangedFrames;
        ConfidenceBox.IsChecked = s.Overlay.ShowConfidence;
        IdleBox.IsChecked = s.Overlay.ShowIdleIndicator;

        NextToSignalBox.IsChecked = s.Overlay.Placement == OverlayPlacement.NextToSignal;
        XSlider.IsEnabled = YSlider.IsEnabled = s.Overlay.Placement == OverlayPlacement.Manual;
        XSlider.Value = s.Overlay.XPercent;
        YSlider.Value = s.Overlay.YPercent;
        ScaleSlider.Value = s.Overlay.Scale;
        OpacitySlider.Value = s.Overlay.Opacity;

        var h = s.Hotkeys;
        HotkeysText.Text =
            $"{h.ToggleOverlay,-14} show / hide overlay\n{h.DebugSnapshot,-14} debug snapshot of this moment (→ debug_snapshots)\n" +
            $"{h.MoveOverlay,-14} move overlay (drag, press again to lock)\n{h.QuickScan,-14} scan now\n" +
            $"{h.PauseOcr,-14} scanning on / off\n{h.Calibration,-14} calibrate search area\n" +
            $"{h.DebugWindow,-14} debug window\n{h.CycleContext,-14} location context Auto / Space / Surface";
        _loading = false;
    }

    /// <summary>Live refresh (about once per second while the panel is open).</summary>
    public void Refresh(PanelStatus st)
    {
        (StateBar.Background, StateDot.Fill) = st.State switch
        {
            PanelState.Running => (OkBar, Ok),
            PanelState.Paused => (WarnBar, Warn),
            _ => (BadBar, Bad),
        };
        StateText.Text = st.Headline;
        (StartButton.Content, StartButton.Background) =
            st.Countdown > 0 ? ($"✕  CANCEL  ({st.Countdown})", WarnButton)
            : st.Scanning ? ("■  STOP", StopButton)
            : ("▶  START", StartGreen);
        LiveText.Text = st.Live;
        if (st.Preview is not null) PreviewImage.Source = st.Preview;
        PreviewHint.Visibility = PreviewImage.Source is null ? Visibility.Visible : Visibility.Collapsed;
        HealthList.ItemsSource = st.Health.Select(h => new
        {
            Mark = h.Ok ? "✓" : "✗",
            Color = h.Ok ? Ok : Bad,
            Title = h.Title + ":",
            h.Detail,
        }).ToList();

        _loading = true;
        OverlaySwitch.IsChecked = st.OverlayVisible;
        _loading = false;
    }

    // ───────────────────────── switches (apply immediately) ─────────────────────────

    private void Start_Click(object sender, RoutedEventArgs e) => _host.StartStop();

    // ───────────────────────── sound alert for specific signatures ─────────────────────────

    private void AlertRs_Commit(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _host.Settings;
        var values = RsAlert.Parse(AlertRsBox.Text);
        var multiples = AlertMultiplesBox.IsChecked == true;
        if (!values.SequenceEqual(s.AlertRsValues) || multiples != s.AlertRsMultiples)
        {
            s.AlertRsValues = values;
            s.AlertRsMultiples = multiples;
            _host.ApplySettings();
        }
        AlertRsBox.Text = RsAlert.Format(values); // normalized ("3,585" → "3585")
        UpdateAlertHint(s);
    }

    private void AlertRsBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) AlertRs_Commit(sender, e);
    }

    private void TestAlert_Click(object sender, RoutedEventArgs e) => AppHost.PlayRsAlertTone();

    private void UpdateAlertHint(AppSettings s) =>
        AlertRsHint.Text = s.AlertRsValues.Count == 0
            ? "Off. Type a signature (e.g. 3585 for Gold, 3870 for Tungsten) and press Enter."
            : "Active: " + _host.DescribeAlertValues(s.AlertRsValues) + (s.AlertRsMultiples ? "   · multiples too" : "") +
              "   — beeps once each time one is detected (works even with Sound alerts off).";

    private void OverlaySwitch_Click(object sender, RoutedEventArgs e) => _host.SetOverlayVisible(OverlaySwitch.IsChecked == true);

    private void Feature_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _host.Settings;
        s.Ocr.AutoLocate = LocateSwitch.IsChecked == true;
        s.HotkeysEnabled = HotkeySwitch.IsChecked == true;
        s.SoundAlerts = SoundSwitch.IsChecked == true;
        s.TargetOnly = TargetSwitch.IsChecked == true;
        s.SaveMissedReads = MissedSwitch.IsChecked == true;
        s.DebugMode = DebugSwitch.IsChecked == true;
        s.Overlay.HideFromCapture = CaptureSwitch.IsChecked == true;
        s.ShowPanelOnStartup = StartupSwitch.IsChecked == true;
        s.AutoStart = AutoStartSwitch.IsChecked == true;
        s.MinimizePanelOnStart = MinimizeSwitch.IsChecked == true;
        _host.ApplySettings();
    }

    // ───────────────────────── advanced ─────────────────────────

    private static void Select(ComboBox box, string tag)
    {
        foreach (ComboBoxItem item in box.Items)
            if ((string)item.Tag == tag) { box.SelectedItem = item; return; }
        box.SelectedIndex = 0;
    }

    private static int ParseInt(string text, int fallback, int min, int max) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? Math.Clamp(v, min, max) : fallback;

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        var s = _host.Settings;
        s.GameVersion = VersionBox.SelectedItem as string ?? MaterialDatabaseLoader.DefaultVersion;
        s.MonitorIndex = Math.Max(0, MonitorBox.SelectedIndex);
        s.Matcher.Context = Enum.Parse<ScanContext>((string)((ComboBoxItem)ContextBox.SelectedItem).Tag);
        s.OcrIntervalMs = int.Parse((string)((ComboBoxItem)IntervalBox.SelectedItem).Tag, CultureInfo.InvariantCulture);
        if (double.TryParse(ToleranceBox.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var tol))
            s.Matcher.MaxAbsoluteError = Math.Clamp(tol, 0, 50);
        s.VoteWindow = ParseInt(VoteWindowBox.Text, s.VoteWindow, 1, 20);
        s.VoteMinAgreement = ParseInt(VoteMinBox.Text, s.VoteMinAgreement, 1, s.VoteWindow);
        s.SkipUnchangedFrames = SkipBox.IsChecked == true;
        s.TargetMaterials = TargetsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        ReadOverlay(s);
        _host.ApplySettings();
        Load(s);
    }

    private void Placement_Click(object sender, RoutedEventArgs e)
    {
        XSlider.IsEnabled = YSlider.IsEnabled = NextToSignalBox.IsChecked != true;
        ReadOverlay(_host.Settings);
        _host.PreviewOverlay();
    }

    private void ReadOverlay(AppSettings s)
    {
        s.Overlay.Placement = NextToSignalBox.IsChecked == true ? OverlayPlacement.NextToSignal : OverlayPlacement.Manual;
        s.Overlay.ShowIdleIndicator = IdleBox.IsChecked == true;
        s.Overlay.ShowConfidence = ConfidenceBox.IsChecked == true;
        s.Overlay.XPercent = XSlider.Value;
        s.Overlay.YPercent = YSlider.Value;
        s.Overlay.Scale = ScaleSlider.Value;
        s.Overlay.Opacity = OpacitySlider.Value;
    }

    private void Overlay_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || !IsLoaded) return;
        ReadOverlay(_host.Settings);
        _host.PreviewOverlay();
    }

    // ───────────────────────── buttons ─────────────────────────

    private void Reload_Click(object sender, RoutedEventArgs e) => _host.Reload();
    private void Restart_Click(object sender, RoutedEventArgs e) => _host.RestartApp();
    private void Calibrate_Click(object sender, RoutedEventArgs e) => _host.StartCalibration();
    private void Move_Click(object sender, RoutedEventArgs e) => _host.ToggleMoveOverlay();
    private void Debug_Click(object sender, RoutedEventArgs e) => _host.ShowDebug();
    private void Snapshot_Click(object sender, RoutedEventArgs e) => _host.TakeDebugSnapshot();
    private void OpenSnapshots_Click(object sender, RoutedEventArgs e) => _host.OpenSnapshotsFolder();
    private void Analyze_Click(object sender, RoutedEventArgs e) => _host.AnalyzeScreenshot();
    private void OpenData_Click(object sender, RoutedEventArgs e) => _host.OpenDataFolder();
    private void OpenConfig_Click(object sender, RoutedEventArgs e) => _host.OpenConfigFolder();
    private void OpenDiagnostics_Click(object sender, RoutedEventArgs e) => _host.OpenDiagnosticsFolder();
    private void SelfTest_Click(object sender, RoutedEventArgs e) => _host.RunSelfTest();
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    private void Exit_Click(object sender, RoutedEventArgs e) => _host.Exit();

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}
