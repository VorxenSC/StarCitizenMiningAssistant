using System.Windows;
using MiningAssistant.Core.Updates;

namespace MiningAssistant.App.Services;

/// <summary>
/// Automatic updates (see <see cref="Updater"/>). Asks once for consent, checks GitHub at start and every
/// 6 hours, downloads only while scanning is stopped (never in the middle of mining), and installs the
/// verified download at the next start — or right away with "Install update now".
/// </summary>
public sealed partial class AppHost
{
    private readonly System.Windows.Threading.DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private UpdateManifest? _updateWaiting;   // newer version found, download postponed while scanning
    private string _updateStatus = "";
    private bool _updateProblem;
    private bool _updateBusy;

    /// <summary>Set by App when this start comes right after an update ("--updated" / "--update-failed").</summary>
    public string? UpdateOutcome { get; init; }

    public bool UpdatesEnabled => Settings.Updates.Enabled == true;
    private UpdateManifest? _staged = Updater.Staged();
    /// <summary>Downloaded, verified version waiting for the next start (cached; refreshed after each download).</summary>
    public UpdateManifest? StagedUpdate => _staged;

    private void InitUpdates()
    {
        if (UpdateOutcome == "--updated")
        {
            Flash($"✓ updated to v{Updater.CurrentVersion}", 5);
            Log($"Updated to v{Updater.CurrentVersion}.");
        }
        else if (UpdateOutcome == "--update-failed")
        {
            Log("The last update could not be installed and was rolled back.");
            Ui.BeginInvoke(() => MessageBox.Show("The update could not be installed, so the previous version was restored.\n\nIt will try again later; details are in config/app.log.",
                "Mining Assistant by Vorxen — update", MessageBoxButton.OK, MessageBoxImage.Warning));
        }

        // The copy we updated from may still have been running a moment ago; tidy up a bit later.
        _ = Task.Delay(TimeSpan.FromSeconds(20)).ContinueWith(_ => { if (Updater.Staged() is null) Updater.CleanUp(); });

        _updateTimer.Tick += (_, _) => _ = CheckForUpdatesAsync(manual: false);
        _updateTimer.Start();

        Ui.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, async () =>
        {
            if (Settings.Updates.Enabled is null) AskUpdateConsent();
            await Task.Delay(TimeSpan.FromSeconds(15));
            await CheckForUpdatesAsync(manual: false);
        });
    }

    /// <summary>Checking GitHub is the app's only network access, so the user decides once.</summary>
    private void AskUpdateConsent()
    {
        var answer = MessageBox.Show(
            "Keep Mining Assistant up to date automatically?\n\n" +
            $"The app will check github.com/{Settings.Updates.Repository} for a new version (at start and every 6 hours), " +
            "download it while scanning is stopped, and install it the next time you open the app.\n\n" +
            "• Only the public release files are downloaded; nothing about you or your PC is sent.\n" +
            "• Every update is digitally signed by Vorxen and checked before it is installed.\n" +
            "• This is the app's only internet access. You can turn it off any time (Functions → Automatic updates).",
            "Mining Assistant by Vorxen — updates", MessageBoxButton.YesNo, MessageBoxImage.Question);
        Settings.Updates.Enabled = answer == MessageBoxResult.Yes;
        Paths.SaveSettings(Settings);
        Log($"Automatic updates {(UpdatesEnabled ? "turned on" : "declined")} by the user.");
        UpdateStatus();
    }

    public void SetUpdatesEnabled(bool on)
    {
        Settings.Updates.Enabled = on;
        Paths.SaveSettings(Settings);
        UpdateStatus();
        if (on) _ = CheckForUpdatesAsync(manual: false);
    }

    /// <summary>Manual check (Advanced settings) works even with automatic updates off.</summary>
    public void CheckForUpdatesNow() => _ = CheckForUpdatesAsync(manual: true);

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if ((!UpdatesEnabled && !manual) || _updateBusy) return;
        _updateBusy = true;
        _updateProblem = false;
        try
        {
            if (_staged is { } staged)
            {
                _updateStatus = $"v{staged.Version} is ready — it installs at the next start (or Advanced settings → Install update now)";
                return;
            }
            _updateStatus = "checking GitHub…";
            UpdateStatus();
            var m = await Updater.CheckAsync(Settings.Updates.Repository);
            Settings.Updates.LastCheck = DateTime.Now;
            Paths.SaveSettings(Settings);
            if (m is null)
            {
                _updateStatus = $"up to date (v{Updater.CurrentVersion}, checked {DateTime.Now:HH:mm})";
                if (manual) Flash($"✓ up to date (v{Updater.CurrentVersion})", 3);
                return;
            }
            Log($"Update available: v{m.Version}.");
            if (IsScanning || _countdown > 0)
            {
                _updateWaiting = m;
                _updateStatus = $"v{m.Version} available — downloads when you stop scanning";
                return;
            }
            await StageAsync(m);
        }
        catch (Exception ex)
        {
            _updateProblem = true;
            _updateStatus = "could not check for updates: " + ex.Message;
            Log("Update check failed: " + ex.Message);
            if (manual) Flash("⚠ update check failed (see the checklist)", 4);
        }
        finally
        {
            _updateBusy = false;
            UpdateStatus();
        }
    }

    private async Task StageAsync(UpdateManifest m)
    {
        _updateStatus = $"downloading v{m.Version}…";
        UpdateStatus();
        var repo = Settings.Updates.Repository;
        await Task.Run(() => Updater.StageAsync(repo, m)); // hashing + unzipping off the UI thread
        _staged = Updater.Staged();
        _updateWaiting = null;
        _updateStatus = $"v{m.Version} is ready — it installs at the next start (or Advanced settings → Install update now)";
        Flash($"⬇ update v{m.Version} ready — installs next start", 5);
        Log($"Update v{m.Version} downloaded and verified.");
    }

    /// <summary>Called when scanning stops: a postponed download can run now.</summary>
    private async void OnScanningStopped()
    {
        if (_updateWaiting is not { } m || _updateBusy) return;
        _updateBusy = true;
        try { await StageAsync(m); }
        catch (Exception ex) { _updateProblem = true; _updateStatus = "download failed: " + ex.Message; Log("Update download failed: " + ex.Message); }
        finally { _updateBusy = false; UpdateStatus(); }
    }

    /// <summary>"Install update now": restart; the next start applies the staged version.</summary>
    public void InstallUpdateNow()
    {
        if (_staged is null) { Flash("No update is waiting — click Check for updates first", 3); return; }
        Log("Installing the staged update now.");
        RestartApp();
    }

    private HealthItem UpdateHealth() => Settings.Updates.Enabled switch
    {
        null => new(true, "Updates", "automatic updates not set up yet"),
        false => new(true, "Updates", $"automatic updates off (v{Updater.CurrentVersion}) — Advanced settings → Check for updates"),
        _ when _updateProblem => new(true, "Updates", _updateStatus + " — it will try again later", Notice: true),
        _ => new(true, "Updates", _updateStatus.Length > 0 ? _updateStatus : $"on (v{Updater.CurrentVersion})"),
    };
}
