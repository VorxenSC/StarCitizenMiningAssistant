using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MiningAssistant.App.Interop;
using MiningAssistant.App.Services;
using MiningAssistant.Core.Roi;
using MiningAssistant.Vision.Capture;

namespace MiningAssistant.App.Windows;

/// <summary>
/// Full-screen window over the game monitor showing a frozen screenshot. The user draws one box
/// per region; boxes are stored as fractions of the monitor so they adapt to other resolutions.
/// </summary>
public partial class CalibrationWindow : Window
{
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0x4A, 0xDE, 0x80), Color.FromRgb(0x22, 0xD3, 0xEE), Color.FromRgb(0xFA, 0xCC, 0x15),
        Color.FromRgb(0xFB, 0x92, 0x3C), Color.FromRgb(0xC0, 0x84, 0xFC), Color.FromRgb(0xF4, 0x72, 0xB6),
    };

    private readonly MonitorInfo _monitor;
    private readonly Dictionary<string, NormalizedRect> _regions;
    private string _selected = RoiNames.RS;
    private Point? _dragStart;
    private Rectangle? _dragRect;

    public RoiProfile Result { get; }

    public CalibrationWindow(MonitorInfo monitor, System.Drawing.Bitmap screenshot, RoiProfile profile)
    {
        InitializeComponent();
        _monitor = monitor;
        Result = profile;
        _regions = new Dictionary<string, NormalizedRect>(profile.Regions, StringComparer.OrdinalIgnoreCase);
        Shot.Source = Imaging.FromBitmap(screenshot);
        Info.Text = $"{monitor.Width}×{monitor.Height}";

        foreach (var (i, (key, label)) in RoiNames.All.Select((r, i) => (i, r)))
        {
            var rb = new RadioButton
            {
                Content = label, GroupName = "region", Tag = key, IsChecked = key == _selected,
                Foreground = new SolidColorBrush(Palette[i % Palette.Length]), Margin = new Thickness(0, 0, 16, 0),
                FontSize = 13, FontWeight = FontWeights.SemiBold,
            };
            rb.Checked += (_, _) => _selected = key;
            RegionButtons.Children.Add(rb);
        }

        SourceInitialized += (_, _) => Win32.MoveTo(this, monitor.Left, monitor.Top, monitor.Width, monitor.Height);
        Loaded += (_, _) => { Activate(); Redraw(); };
        SizeChanged += (_, _) => Redraw();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Cancel_Click(this, e); };
    }

    private Color ColorOf(string key)
    {
        var idx = RoiNames.All.ToList().FindIndex(r => r.Key == key);
        return Palette[Math.Max(0, idx) % Palette.Length];
    }

    private void Redraw()
    {
        Surface.Children.Clear();
        double w = Surface.ActualWidth, h = Surface.ActualHeight;
        if (w <= 0 || h <= 0) return;

        foreach (var (key, r) in _regions)
        {
            if (!r.IsValid) continue;
            var color = ColorOf(key);
            var rect = new Rectangle
            {
                Width = r.WidthPercent * w, Height = r.HeightPercent * h,
                Stroke = new SolidColorBrush(color), StrokeThickness = 2,
                Fill = new SolidColorBrush(Color.FromArgb(40, color.R, color.G, color.B)),
            };
            Canvas.SetLeft(rect, r.XPercent * w);
            Canvas.SetTop(rect, r.YPercent * h);
            Surface.Children.Add(rect);

            var label = RoiNames.All.FirstOrDefault(x => x.Key == key).Label ?? key;
            var px = r.ToPixels(_monitor.Width, _monitor.Height);
            var tb = new TextBlock
            {
                Text = $"{label}  {px.Width}×{px.Height}px", Foreground = Brushes.Black, FontWeight = FontWeights.SemiBold,
                Background = new SolidColorBrush(color), Padding = new Thickness(4, 1, 4, 1), FontSize = 11,
            };
            Canvas.SetLeft(tb, r.XPercent * w);
            Canvas.SetTop(tb, Math.Max(0, r.YPercent * h - 18));
            Surface.Children.Add(tb);
        }
    }

    private void Surface_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Surface);
        var c = ColorOf(_selected);
        _dragRect = new Rectangle { Stroke = new SolidColorBrush(c), StrokeThickness = 2, StrokeDashArray = { 4, 2 } };
        Surface.Children.Add(_dragRect);
        Surface.CaptureMouse();
    }

    private void Surface_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } s || _dragRect is null) return;
        var p = e.GetPosition(Surface);
        Canvas.SetLeft(_dragRect, Math.Min(s.X, p.X));
        Canvas.SetTop(_dragRect, Math.Min(s.Y, p.Y));
        _dragRect.Width = Math.Abs(p.X - s.X);
        _dragRect.Height = Math.Abs(p.Y - s.Y);
    }

    private void Surface_MouseUp(object sender, MouseButtonEventArgs e)
    {
        Surface.ReleaseMouseCapture();
        if (_dragStart is not { } s) return;
        var p = e.GetPosition(Surface);
        _dragStart = null;
        double w = Surface.ActualWidth, h = Surface.ActualHeight;
        var x0 = Math.Clamp(Math.Min(s.X, p.X), 0, w);
        var y0 = Math.Clamp(Math.Min(s.Y, p.Y), 0, h);
        var x1 = Math.Clamp(Math.Max(s.X, p.X), 0, w);
        var y1 = Math.Clamp(Math.Max(s.Y, p.Y), 0, h);
        if (x1 - x0 >= 6 && y1 - y0 >= 6)
            _regions[_selected] = new NormalizedRect(x0 / w, y0 / h, (x1 - x0) / w, (y1 - y0) / h);
        Redraw();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _regions.Remove(_selected);
        Redraw();
    }

    private void MoveToolbar_Click(object sender, RoutedEventArgs e) =>
        Toolbar.VerticalAlignment = Toolbar.VerticalAlignment == VerticalAlignment.Top ? VerticalAlignment.Bottom : VerticalAlignment.Top;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        Result.Regions = new Dictionary<string, NormalizedRect>(_regions, StringComparer.OrdinalIgnoreCase);
        Result.CalibratedWidth = _monitor.Width;
        Result.CalibratedHeight = _monitor.Height;
        Result.CalibratedAt = DateTime.UtcNow;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
