using System.Windows;
using MiningAssistant.Core.Licensing;

namespace MiningAssistant.App.Windows;

/// <summary>First-run activation: name + name-bound key (see <see cref="LicenseKey"/>).</summary>
public partial class ActivationWindow : Window
{
    private int _attempts;

    public License? Result { get; private set; }

    public ActivationWindow(string? previousName = null)
    {
        InitializeComponent();
        Controls.VorxenChrome.Attach(this);
        NameBox.Text = previousName ?? "";
        Loaded += (_, _) => (NameBox.Text.Length == 0 ? NameBox : KeyBox).Focus();
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { ErrorText.Text = "Enter your name."; NameBox.Focus(); return; }
        if (LicenseKey.NormalizeKey(KeyBox.Text) is not { } key) { ErrorText.Text = "The key has 16 characters: XXXX-XXXX-XXXX-XXXX."; KeyBox.Focus(); return; }

        if (!LicenseKey.IsValid(name, key))
        {
            // Small, growing delay after each wrong try: makes guessing pointless, harmless for typos.
            _attempts++;
            IsEnabled = false;
            await Task.Delay(Math.Min(400 * _attempts, 4000));
            IsEnabled = true;
            ErrorText.Text = "This key does not match this name. Check both — the name must be exactly the one the key was made for.";
            KeyBox.Focus();
            KeyBox.SelectAll();
            return;
        }

        Result = new License { Name = name, Key = key, ActivatedAt = DateTime.Now };
        DialogResult = true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
