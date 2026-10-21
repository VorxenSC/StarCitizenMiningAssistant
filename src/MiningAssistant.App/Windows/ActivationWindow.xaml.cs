using System.Windows;
using MiningAssistant.Core.Licensing;

namespace MiningAssistant.App.Windows;

/// <summary>First-run activation: name + key made for that name on this PC (see <see cref="LicenseKey"/>).</summary>
public partial class ActivationWindow : Window
{
    private int _attempts;

    public License? Result { get; private set; }

    private readonly string _pcCode;

    public ActivationWindow(string? previousName, string pcCode, string? notice = null)
    {
        InitializeComponent();
        Controls.VorxenChrome.Attach(this);
        _pcCode = pcCode;
        PcCodeText.Text = pcCode;
        NameBox.Text = previousName ?? "";
        ErrorText.Text = notice ?? "";
        Loaded += (_, _) => (NameBox.Text.Length == 0 ? NameBox : KeyBox).Focus();
    }

    private async void Activate_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (name.Length == 0) { ErrorText.Text = "Enter your name."; NameBox.Focus(); return; }
        if (LicenseKey.NormalizeKey(KeyBox.Text) is not { } key) { ErrorText.Text = "The key has 16 characters: XXXX-XXXX-XXXX-XXXX."; KeyBox.Focus(); return; }

        if (!LicenseKey.IsValid(name, key, _pcCode))
        {
            // Small, growing delay after each wrong try: makes guessing pointless, harmless for typos.
            _attempts++;
            IsEnabled = false;
            await Task.Delay(Math.Min(400 * _attempts, 4000));
            IsEnabled = true;
            ErrorText.Text = "This key does not match this name on this PC. Check the name (exactly as sent to Vorxen) — a key made for another PC does not work here.";
            KeyBox.Focus();
            KeyBox.SelectAll();
            return;
        }

        Result = new License { Name = name, Key = key, PcCode = _pcCode, ActivatedAt = DateTime.Now };
        DialogResult = true;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>Copies "name + PC code" ready to paste in Discord / a message to Vorxen.</summary>
    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        try
        {
            Clipboard.SetText(name.Length > 0 ? $"Mining Assistant key request — Name: {name} — PC code: {_pcCode}" : _pcCode);
            CopyLabel.Text = "Copied!";
        }
        catch
        {
            CopyLabel.Text = "Copy failed — select the code";
        }
    }
}
