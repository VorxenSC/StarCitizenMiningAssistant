using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using MiningAssistant.App.Interop;
using MiningAssistant.App.Services;
using MiningAssistant.Core.Materials;
using MiningAssistant.Vision.Capture;

namespace MiningAssistant.App.Windows;

/// <summary>An unknown RS waiting for the user to say what it is.</summary>
public sealed record UnknownItem(int RS, DateTime At, byte[]? PlatePng);

/// <summary>What the user answered.</summary>
public sealed record UnknownAnswer(UnknownItem Item, string Name, int Count, SignalKind Kind, string Notes);

/// <summary>
/// Persistent pop-up that asks what an unknown signature is. It stays on screen (it does not vanish
/// with the overlay card) until the user saves, ignores or chooses "never ask for this RS". Several
/// unknowns queue up in the same window. It never takes focus from the game by itself.
/// </summary>
public partial class UnknownSignatureWindow : Window
{
    private readonly Queue<UnknownItem> _queue = new();
    private UnknownItem? _current;

    public event Action<UnknownAnswer>? Saved;
    public event Action<UnknownItem>? NeverAsk;

    public UnknownSignatureWindow(IEnumerable<string> knownNames)
    {
        InitializeComponent();
        NameBox.ItemsSource = knownNames.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
        Closing += (_, e) => { e.Cancel = true; Ignore_Click(this, new RoutedEventArgs()); };
    }

    public int Pending => _queue.Count + (_current is null ? 0 : 1);

    public void Enqueue(UnknownItem item, MonitorInfo monitor)
    {
        if (_current?.RS == item.RS || _queue.Any(q => q.RS == item.RS)) return;
        if (_current is null) ShowItem(item);
        else _queue.Enqueue(item);
        UpdateQueueText();

        if (!IsVisible)
        {
            Show();
            // Right side of the game monitor, a bit below the middle: away from the RS plate and the HUD center.
            var (_, _, w, _) = Win32.Bounds(this);
            Win32.MoveTo(this, monitor.Left + monitor.Width - w - 40, monitor.Top + (int)(monitor.Height * 0.45));
        }
    }

    /// <summary>Dev check (--render-preview): lay the window out off-screen with a sample item and save it as PNG.</summary>
    public void RenderPreview(UnknownItem sample, string pngPath)
    {
        ShowItem(sample);
        _queue.Enqueue(sample with { RS = sample.RS + 1 });
        UpdateQueueText();
        Left = -20000;
        Top = -20000;
        Show();
        UpdateLayout();
        var content = (FrameworkElement)Content;
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap((int)ActualWidth, (int)content.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        var bg = new System.Windows.Media.DrawingVisual();
        using (var dc = bg.RenderOpen()) dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, content.ActualHeight));
        rtb.Render(bg);
        rtb.Render(content);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(pngPath);
        enc.Save(fs);
        Hide();
    }

    private void ShowItem(UnknownItem item)
    {
        _current = item;
        RsText.Text = $"RS {item.RS.ToString("N0", CultureInfo.InvariantCulture)}";
        WhenText.Text = $"seen at {item.At:HH:mm:ss}";
        PlateImage.Source = item.PlatePng is { Length: > 0 } png ? Imaging.FromPng(png) : null;
        NameBox.Text = "";
        CountBox.Text = "1";
        NotesBox.Text = "";
        KindBox.SelectedIndex = 0;
        ErrorText.Text = "";
        NeverButton.Content = $"Never ask for RS {item.RS.ToString("N0", CultureInfo.InvariantCulture)}";
        UpdateBase();
    }

    private void Next()
    {
        if (_queue.Count > 0) ShowItem(_queue.Dequeue());
        else
        {
            _current = null;
            Hide();
        }
        UpdateQueueText();
    }

    private void UpdateQueueText() => QueueText.Text = Pending > 1 ? $"1 of {Pending}" : "";

    private int Count => int.TryParse(CountBox.Text.Trim(), out var n) && n is >= 1 and <= 99 ? n : 0;

    private void UpdateBase()
    {
        if (_current is null) return;
        BaseText.Text = Count > 0
            ? $"→ base RS {LearnedSignature.ComputeBase(_current.RS, Count):N0} per {(Count == 1 ? "rock" : "rock / deposit")}"
            : "Enter a number from 1 to 99.";
    }

    private void CountBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateBase();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_current is null) return;
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { ErrorText.Text = "Type what it is (or click Ignore)."; return; }
        if (Count == 0) { ErrorText.Text = "How many rocks: enter a number from 1 to 99."; return; }
        var kind = Enum.Parse<SignalKind>((string)((ComboBoxItem)KindBox.SelectedItem).Tag);
        Saved?.Invoke(new UnknownAnswer(_current, name, Count, kind, NotesBox.Text.Trim()));
        Next();
    }

    private void Ignore_Click(object sender, RoutedEventArgs e) => Next();

    private void Never_Click(object sender, RoutedEventArgs e)
    {
        if (_current is not null) NeverAsk?.Invoke(_current);
        Next();
    }
}
