using System.Windows;
using System.Windows.Media;

namespace MiningAssistant.App.Windows;

/// <summary>One line of "what we detected on this PC".</summary>
public sealed record DetectedCondition(bool Ok, string Text);

/// <summary>
/// "Before you start": the conditions the app needs (display mode, resolution, clear center, HUD colors,
/// monitor, admin rights, start flow, Windows version), plus what was detected on this PC.
/// Shown right after activation and from the control panel (Requirements).
/// </summary>
public partial class RequirementsWindow : Window
{
    public event Action? SelfTestRequested;

    public RequirementsWindow(string userName, IEnumerable<DetectedCondition> detected)
    {
        InitializeComponent();
        if (userName.Length > 0) WelcomeRun.Text = $"   Welcome {userName}, Let's Rock and Roll!";
        var ok = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)));
        var warn = Freeze(new SolidColorBrush(Color.FromRgb(0xFA, 0xCC, 0x15)));
        DetectedList.ItemsSource = detected.Select(d => new { Mark = d.Ok ? "✓" : "⚠", Color = d.Ok ? ok : warn, d.Text }).ToList();
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();

    private void SelfTest_Click(object sender, RoutedEventArgs e) => SelfTestRequested?.Invoke();
}
