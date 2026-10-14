using System.Windows;
using MiningAssistant.App.Interop;
using MiningAssistant.Vision.Capture;

namespace MiningAssistant.App.Windows;

/// <summary>
/// Big "starting in 5…1" notice shown in the middle of the game monitor after the user clicks Start,
/// giving them time to switch to the game window. Click-through and never focused, like the overlay.
/// </summary>
public partial class CountdownWindow : Window
{
    private MonitorInfo? _monitor;
    private readonly bool _hideFromCapture;

    /// <param name="hideFromCapture">Same choice as the overlay (the scanner is not running during the countdown anyway).</param>
    public CountdownWindow(bool hideFromCapture)
    {
        _hideFromCapture = hideFromCapture;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            Win32.MakeOverlay(this);
            Win32.SetHiddenFromCapture(this, _hideFromCapture);
        };
        SizeChanged += (_, _) => Center();
    }

    public void ShowNumber(int seconds, MonitorInfo monitor)
    {
        _monitor = monitor;
        Number.Text = seconds.ToString();
        Caption.Text = seconds == 1 ? "SCANNING STARTS IN 1 SECOND" : $"SCANNING STARTS IN {seconds} SECONDS";
        if (!IsVisible) Show();
        Center();
    }

    private void Center()
    {
        if (_monitor is not { } m) return;
        var (_, _, w, h) = Win32.Bounds(this);
        Win32.MoveTo(this, m.Left + (m.Width - w) / 2, m.Top + (m.Height - h) / 2);
    }
}
