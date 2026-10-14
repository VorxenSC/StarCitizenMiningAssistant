using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MiningAssistant.App.Controls;
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
    // Status bar colors from the design: (background, border, dot, text) per state.
    private static readonly (Brush Bg, Brush Border, Brush Dot, Brush Text) RunningLook =
        (Frozen(0x0F, 0x2A, 0x22), Frozen(0x1F, 0x5A, 0x44), Frozen(0x3D, 0xDC, 0x97), Frozen(0x9B, 0xF3, 0xC8));
    private static readonly (Brush Bg, Brush Border, Brush Dot, Brush Text) PausedLook =
        (Frozen(0x2A, 0x23, 0x14), Frozen(0x5C, 0x4A, 0x1E), Frozen(0xF2, 0xB3, 0x3D), Frozen(0xF6, 0xCF, 0x7E));
    private static readonly (Brush Bg, Brush Border, Brush Dot, Brush Text) ProblemLook =
        (Frozen(0x2A, 0x15, 0x19), Frozen(0x66, 0x30, 0x3A), Frozen(0xFF, 0x9A, 0xA0), Frozen(0xFF, 0xC2, 0xC6));

    private static readonly Brush StopRed = Frozen(0xC7, 0x3B, 0x42), CancelAmber = Frozen(0x5C, 0x4A, 0x1E), White = Frozen(0xFF, 0xFF, 0xFF);

    private readonly AppHost _host;
    private readonly Storyboard _pulse = new() { RepeatBehavior = RepeatBehavior.Forever };
    private bool _loading;

    public SettingsWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        VorxenChrome.Attach(this);
        VersionText.Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?");
        if (host.LicensedTo.Length > 0)
        {
            WelcomeText.Inlines.Add(new Run("Welcome "));
            WelcomeText.Inlines.Add(new Italic(new Run(host.LicensedTo)));
            WelcomeText.Inlines.Add(new Run(", Let's Rock and Roll!"));
        }
        BuildPulse();
        Closing += (_, e) => { e.Cancel = true; host.Paths.SaveSettings(host.Settings); Hide(); };

        // Resizable: never taller than the screen's work area; the size the user picks is remembered.
        MaxHeight = SystemParameters.WorkArea.Height;
        if (host.Settings.PanelHeight > 0)
        {
            SizeToContent = SizeToContent.Manual;
            Height = Math.Clamp(host.Settings.PanelHeight, MinHeight, MaxHeight);
            Width = Math.Max(MinWidth, host.Settings.PanelWidth > 0 ? host.Settings.PanelWidth : Width);
        }
        SizeChanged += (_, _) =>
        {
            // WPF switches SizeToContent to Manual as soon as the user drags an edge.
            if (!IsLoaded || SizeToContent != SizeToContent.Manual || WindowState != WindowState.Normal) return;
            host.Settings.PanelHeight = ActualHeight;
            host.Settings.PanelWidth = ActualWidth;
        };
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
        AskUnknownSwitch.IsChecked = s.AskForUnknownSignatures;
        ContributeSwitch.IsChecked = s.Contribute.Enabled;
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
        HotkeyList.ItemsSource = new[]
        {
            new { Key = h.ToggleOverlay, Action = "Show / hide the overlay" },
            new { Key = h.DebugSnapshot, Action = "Debug snapshot of this moment" },
            new { Key = h.MoveOverlay, Action = "Move the overlay (drag, press again to lock)" },
            new { Key = h.QuickScan, Action = "Scan now" },
            new { Key = h.PauseOcr, Action = "Scanning on / off" },
            new { Key = h.Calibration, Action = "Calibrate the search area" },
            new { Key = h.DebugWindow, Action = "Debug window" },
            new { Key = h.CycleContext, Action = "Location: Auto / Space / Surface" },
        }.Where(k => !string.IsNullOrWhiteSpace(k.Key)).ToList();
        _loading = false;
    }

    /// <summary>Live refresh (about once per second while the panel is open).</summary>
    public void Refresh(PanelStatus st)
    {
        var look = st.State switch
        {
            PanelState.Running => RunningLook,
            PanelState.Paused => PausedLook,
            _ => ProblemLook,
        };
        StateBar.Background = look.Bg;
        StateBar.BorderBrush = look.Border;
        StateDot.Fill = StatePulse.Fill = look.Dot;
        StateText.Foreground = look.Text;
        // "LABEL — detail": the label in bold heading type, the detail a little dimmer.
        var dash = st.Headline.IndexOf(" — ", StringComparison.Ordinal);
        StateLabel.Text = dash < 0 ? st.Headline : st.Headline[..dash];
        StateSub.Text = dash < 0 ? "" : st.Headline[dash..];
        StateSub.Foreground = new SolidColorBrush(((SolidColorBrush)look.Text).Color) { Opacity = 0.85 };

        if (st.Countdown > 0) SetRunButton($"CANCEL ({st.Countdown})", "IconX", CancelAmber, look.Text, stroke: true);
        else if (st.Scanning) SetRunButton("STOP", "IconStop", StopRed, White);
        else SetRunButton("START", "IconPlay", (Brush)FindResource("Accent"), (Brush)FindResource("OnAccent"));

        LiveList.ItemsSource = st.Live;
        if (st.Preview is not null) PreviewImage.Source = st.Preview;
        PreviewHint.Visibility = PreviewImage.Source is null ? Visibility.Visible : Visibility.Collapsed;
        var mon = Monitors.Get(_host.Settings.MonitorIndex);
        PreviewMonitor.Text = $"MONITOR #{mon.Index} · {mon.Width}×{mon.Height}";
        PreviewInterval.Text = $"EVERY {_host.Settings.OcrIntervalMs} MS";
        HealthList.ItemsSource = st.Health.Select(h => new { h.Ok, Title = h.Title + ":", h.Detail }).ToList();

        _loading = true;
        OverlaySwitch.IsChecked = st.OverlayVisible;
        _loading = false;
    }

    private void SetRunButton(string label, string icon, Brush background, Brush foreground, bool stroke = false)
    {
        StartLabel.Text = label;
        StartButton.Background = background;
        StartButton.Foreground = foreground;
        StartIcon.Style = (Style)FindResource(stroke ? "Icon" : "FilledIcon");
        StartIcon.Width = StartIcon.Height = 14;
        StartIcon.Tag = FindResource(icon);
    }

    /// <summary>
    /// The status dot's pulse ring (CSS "pulse" in the design). It only runs while the panel is on
    /// screen, so a minimized / hidden panel costs nothing while the game runs.
    /// </summary>
    private void BuildPulse()
    {
        var duration = new Duration(TimeSpan.FromSeconds(1.8));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        foreach (var prop in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            var grow = new DoubleAnimation(1, 2.6, duration) { EasingFunction = ease };
            Storyboard.SetTarget(grow, StatePulse);
            Storyboard.SetTargetProperty(grow, new PropertyPath($"RenderTransform.{(prop == ScaleTransform.ScaleXProperty ? "ScaleX" : "ScaleY")}"));
            _pulse.Children.Add(grow);
        }
        var fade = new DoubleAnimation(0.7, 0, duration) { EasingFunction = ease };
        Storyboard.SetTarget(fade, StatePulse);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        _pulse.Children.Add(fade);

        void Update()
        {
            var animate = IsVisible && WindowState != WindowState.Minimized && SystemParameters.ClientAreaAnimation;
            if (animate) _pulse.Begin(this, true);
            else _pulse.Stop(this);
        }
        IsVisibleChanged += (_, _) => Update();
        StateChanged += (_, _) => Update();
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

    // ───────────────────────── learned signatures / contributions ─────────────────────────

    /// <summary>Turning sending on asks for explicit consent, showing exactly what leaves the PC.</summary>
    private void Contribute_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var s = _host.Settings;
        if (ContributeSwitch.IsChecked == true && !s.Contribute.Enabled)
        {
            var answer = MessageBox.Show(
                "When you name an unknown signature and tick “Send”, the app will create a folder in\n\n" +
                $"    github.com/{s.Contribute.Repository}  ({s.Contribute.Folder}/)\n\n" +
                "with ONLY:\n" +
                "  • the RS number, the name / count / type / notes you typed, game version, date\n" +
                "  • a small image of the RS plate (pin icon + number)\n\n" +
                "Never the full screen, chat, or anything else. It is sent by the GitHub CLI (gh) logged in on\n" +
                "this PC; the app itself opens no network connection. You can turn this off at any time.\n\n" +
                "Turn on sending?",
                "Send named signatures to GitHub", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                ContributeSwitch.IsChecked = false;
                return;
            }
        }
        s.Contribute.Enabled = ContributeSwitch.IsChecked == true;
        _host.ApplySettings();
        _host.RefreshGitHubStatus();
    }

    private void OpenLearned_Click(object sender, RoutedEventArgs e) => _host.OpenLearnedFile();
    private void ChangeLicense_Click(object sender, RoutedEventArgs e) => _host.ChangeLicense();
    private void SendPending_Click(object sender, RoutedEventArgs e) => _host.SendPendingContributions();

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
        s.AskForUnknownSignatures = AskUnknownSwitch.IsChecked == true;
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
    private void Requirements_Click(object sender, RoutedEventArgs e) => _host.ShowRequirements();
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
