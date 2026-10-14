using System.Windows;
using System.Windows.Media;
using MiningAssistant.App.Interop;
using MiningAssistant.Core.Materials;
using MiningAssistant.Core.Presentation;
using MiningAssistant.Core.Settings;
using MiningAssistant.Vision.Capture;

namespace MiningAssistant.App.Windows;

/// <summary>
/// Transparent, click-through, never-focused overlay. Colors always come with text + icons,
/// so information never depends on color alone.
/// </summary>
public partial class OverlayWindow : Window
{
    private static readonly Brush Green = Brush(0x4A, 0xDE, 0x80);
    private static readonly Brush Cyan = Brush(0x22, 0xD3, 0xEE);
    private static readonly Brush Yellow = Brush(0xFA, 0xCC, 0x15);
    private static readonly Brush Orange = Brush(0xFB, 0x92, 0x3C);
    private static readonly Brush Gray = Brush(0x94, 0xA3, 0xB8);
    private static readonly Brush Violet = Brush(0xC0, 0x84, 0xFC);
    private static readonly Brush Blue = Brush(0x60, 0xA5, 0xFA);
    private static readonly Brush Teal = Brush(0x2D, 0xD4, 0xBF);

    public OverlayWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Win32.MakeOverlay(this);
        SizeChanged += (_, _) => { if (_centerH) Reposition(); };
    }

    private bool _showIdleIndicator = true;

    // ───── move mode: the only time the overlay accepts the mouse ─────
    private bool _moveMode;
    private Point? _dragStart;           // cursor, physical pixels
    private (int x, int y) _dragOrigin;  // window, physical pixels

    public bool MoveMode => _moveMode;

    /// <summary>Raised when move mode ends (drag lock), with the new physical bounds: left, top, width.</summary>
    public event Action<int, int, int>? Moved;

    // Placement anchor (physical px). With _centerH the anchor is the card's horizontal center, and the
    // window is re-positioned whenever its width changes (the card grows/shrinks with its text).
    private int _anchorX, _anchorY;
    private bool _centerH;

    private void Reposition()
    {
        if (_moveMode) return;
        var width = Win32.Bounds(this).width;
        Win32.MoveTo(this, _centerH ? _anchorX - width / 2 : _anchorX, _anchorY);
    }

    /// <summary>
    /// Enables dragging: the window stops being click-through and shows a dashed frame with a sample
    /// card. Disabling restores the click-through overlay and reports the final position.
    /// </summary>
    public void SetMoveMode(bool on)
    {
        if (_moveMode == on) return;
        _moveMode = on;
        if (on)
        {
            Win32.RemoveExStyle(this, Win32.WS_EX_TRANSPARENT);
            IsHitTestVisible = true;
            Opacity = 1;
            Cursor = System.Windows.Input.Cursors.SizeAll;
            if (Card.Visibility != Visibility.Visible)
                ShowView(new OverlayView(OverlayKind.Match, "GOLD", "RS 3,585", "1 ROCK", "★★ VERY HIGH PRIORITY",
                    MaterialPriority.VeryHigh, null, Array.Empty<string>(), false, false, SignalKind.Ore), null);
            Card.BorderBrush = Yellow;
            Card.BorderThickness = new Thickness(2);
            SetLine(StatusText, "⤧ DRAG ME · double-click or Ctrl+Shift+O to lock");
        }
        else
        {
            _dragStart = null;
            Card.ReleaseMouseCapture();
            Card.BorderThickness = new Thickness(1.5);
            IsHitTestVisible = false;
            Cursor = null;
            Win32.AddExStyle(this, Win32.WS_EX_TRANSPARENT);
            SetLine(StatusText, null);
            var (x, y, width, _) = Win32.Bounds(this);
            Moved?.Invoke(x, y, width);
        }
    }

    private void Card_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!_moveMode) return;
        if (e.ClickCount == 2)
        {
            SetMoveMode(false);
            return;
        }
        _dragStart = PointToScreen(e.GetPosition(this)); // device (physical) pixels
        _dragOrigin = Win32.Position(this);
        Card.CaptureMouse();
    }

    private void Card_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_moveMode || _dragStart is not { } start) return;
        var now = PointToScreen(e.GetPosition(this));
        Win32.MoveTo(this, _dragOrigin.x + (int)(now.X - start.X), _dragOrigin.y + (int)(now.Y - start.Y));
    }

    private void Card_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragStart = null;
        Card.ReleaseMouseCapture();
    }

    /// <summary>
    /// Applies look and places the overlay: (screenX, screenY) is the top-left, or the top-center when
    /// <paramref name="centerHorizontally"/> (physical screen pixels).
    /// </summary>
    public void ApplySettings(OverlaySettings s, int screenX, int screenY, bool centerHorizontally)
    {
        if (_moveMode) return; // the user is placing it by hand
        Opacity = Math.Clamp(s.Opacity, 0.2, 1);
        RootScale.ScaleX = RootScale.ScaleY = Math.Clamp(s.Scale, 0.5, 3);
        _showIdleIndicator = s.ShowIdleIndicator;
        Win32.SetHiddenFromCapture(this, s.HideFromCapture);
        (_anchorX, _anchorY, _centerH) = (screenX, screenY, centerHorizontally);
        Reposition();
    }

    public void KeepOnTop() => Win32.BringTopmost(this);

    /// <summary>
    /// Idle state. The plain "scanning" pill is hidden unless enabled (it sits next to the reticle);
    /// status messages (paused, saved, errors) are always shown.
    /// </summary>
    public void ShowIdle(string status, bool isStatusMessage = true)
    {
        Card.Visibility = Visibility.Collapsed;
        IdleText.Text = status;
        IdlePanel.Visibility = isStatusMessage || _showIdleIndicator ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ShowView(OverlayView v, string? status)
    {
        if (v.Kind == OverlayKind.Idle)
        {
            ShowIdle(status ?? "⛏ scanning", isStatusMessage: status is not null);
            return;
        }

        IdlePanel.Visibility = Visibility.Collapsed;
        Card.Visibility = Visibility.Visible;

        var accent = v.Kind switch
        {
            OverlayKind.Ambiguous => Orange,
            OverlayKind.Unknown => Violet,
            OverlayKind.NotTarget => Gray,
            _ => v.Signal switch
            {
                SignalKind.AsteroidType => Blue,
                SignalKind.FpsDeposit or SignalKind.RocDeposit => Teal,
                SignalKind.Salvage => Gray,
                _ => v.Priority switch
                {
                    MaterialPriority.VeryHigh => Green,
                    MaterialPriority.High => Cyan,
                    MaterialPriority.Conditional => Yellow,
                    _ => Gray,
                },
            },
        };

        Card.BorderBrush = accent;
        AccentBar.Background = accent;
        Headline.Foreground = accent;
        PriorityText.Foreground = accent;
        Card.Opacity = v.Kind == OverlayKind.NotTarget || v.Signal == SignalKind.Salvage ? 0.6 : 1;
        SetLine(NoteText, v.Note);

        TargetText.Visibility = v.IsTarget ? Visibility.Visible : Visibility.Collapsed;
        Headline.Text = v.Kind == OverlayKind.Ambiguous ? "⚠ POSSIBLE" : v.Kind == OverlayKind.Unknown ? "? UNKNOWN" : v.Headline;
        SetLine(RsText, v.RsLine);
        SetLine(RocksText, v.RocksLine);
        SetLine(PriorityText, v.PriorityLine);
        SetLine(ConfidenceText, v.ConfidenceLine);
        AltList.ItemsSource = v.Alternatives;
        AltList.Visibility = v.Alternatives.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        HeldText.Text = v.IsHeld ? "last seen" : "";
        ContextText.Text = v.ContextLabel ?? "";
        SetLine(StatusText, status);
    }

    private static void SetLine(System.Windows.Controls.TextBlock tb, string? text)
    {
        tb.Text = text ?? "";
        tb.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private static Brush Brush(byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromRgb(r, g, b));
        br.Freeze();
        return br;
    }
}
