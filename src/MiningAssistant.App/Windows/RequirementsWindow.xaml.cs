using System.Windows;
using System.Windows.Documents;
using MiningAssistant.App.Controls;

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
        VorxenChrome.Attach(this);
        if (userName.Length > 0)
        {
            WelcomeText.Inlines.Add(new Run("Welcome "));
            WelcomeText.Inlines.Add(new Italic(new Run(userName)));
            WelcomeText.Inlines.Add(new Run(", Let's Rock and Roll!"));
        }
        DetectedList.ItemsSource = detected.ToList();
        // Taller than a 1080p screen: never exceed the work area, scroll instead.
        MaxHeight = SystemParameters.WorkArea.Height;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();

    private void SelfTest_Click(object sender, RoutedEventArgs e) => SelfTestRequested?.Invoke();
}
