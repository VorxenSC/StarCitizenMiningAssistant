using MiningAssistant.Core.Matching;
using MiningAssistant.Core.Ocr;

namespace MiningAssistant.Core.Settings;

public enum ScanMode
{
    QuickScan,
    /// <summary>Not implemented in the MVP; reserved for scan-panel parsing.</summary>
    DetailedMining,
}

public enum PreprocessMode
{
    /// <summary>Try white-text isolation first, then grayscale, then binarized, until a known ore matches.</summary>
    Auto,
    Grayscale,
    Binarize,
    /// <summary>Blue channel: white digits on an amber plate (icon and plate vanish).</summary>
    WhiteText,
    /// <summary>min(R,G): amber/yellow digits on teal space.</summary>
    Amber,
}

public sealed class OcrSettings
{
    /// <summary>BCP-47 tag of the Windows OCR language pack. Falls back to user profile languages if missing.</summary>
    public string Language { get; set; } = "en-US";
    /// <summary>
    /// ROI upscale factor before OCR. HUD digits are ~13 px tall at 1440p; 4 gave 10/10 on real
    /// 3440×1440 screenshots (3 confused 5↔6 occasionally).
    /// </summary>
    public int Upscale { get; set; } = 4;
    public PreprocessMode Preprocess { get; set; } = PreprocessMode.Auto;
    /// <summary>
    /// Find the amber RS plate (pin icon + digits) inside the RS region and read only its digits.
    /// The RS region then acts as a generous *search area*, so HUD sway, other resolutions and busy
    /// backgrounds no longer matter. Off = read the whole RS region as text (legacy fixed-area mode).
    /// </summary>
    public bool AutoLocate { get; set; } = true;
    /// <summary>
    /// With auto-locate, the RS region is grown to at least this fraction of the screen (around its
    /// center): a box drawn tight around the number loses the plate as soon as the HUD sways.
    /// </summary>
    public double MinSearchWidth { get; set; } = 0.30;
    public double MinSearchHeight { get; set; } = 0.30;

    /// <summary>The area actually searched for the RS plate.</summary>
    public Roi.NormalizedRect SearchArea(Roi.NormalizedRect region) =>
        AutoLocate ? region.AtLeast(MinSearchWidth, MinSearchHeight) : region;
    /// <summary>Skip OCR when the ROI contains no HUD text at all (saves CPU while nothing is scanned).</summary>
    public bool SkipEmptyRoi { get; set; } = true;
    public NumberNormalizerOptions Normalizer { get; set; } = new();
}

public enum OverlayPlacement
{
    /// <summary>Right next to the in-game RS indicator (anchored to the calibrated RS region).</summary>
    NextToSignal,
    /// <summary>Fixed position from XPercent / YPercent.</summary>
    Manual,
}

public sealed class OverlaySettings
{
    /// <summary>Default: fixed spot on the right, centered under the HUD's "TARGET STATUS" bar.</summary>
    public OverlayPlacement Placement { get; set; } = OverlayPlacement.Manual;

    /// <summary>Manual placement: XPercent is the card's horizontal center (true) or its left edge (false).</summary>
    public bool CenterHorizontally { get; set; } = true;

    /// <summary>
    /// NextToSignal: card top-left relative to the RS region's center, in pixels at 1440p
    /// (scaled with the monitor height). Default puts the card just right of the number,
    /// with the material name level with it.
    /// </summary>
    public int AnchorOffsetX { get; set; } = 70;
    public int AnchorOffsetY { get; set; } = -32;

    /// <summary>Show the faint "scanning" pill when nothing is detected (off by default next to the reticle).</summary>
    public bool ShowIdleIndicator { get; set; } = false;

    /// <summary>Manual placement: top-left position as a fraction of the monitor.</summary>
    // Centered under the "TARGET STATUS" bar (center x ≈ 80.4%), ~1 inch below it
    // (bar bottom ≈ 353 px + ~110 px on a 34" 3440×1440 panel). Measured on the user's screenshot.
    public double XPercent { get; set; } = 0.8035;
    public double YPercent { get; set; } = 0.32;
    public double Opacity { get; set; } = 0.92;
    public double Scale { get; set; } = 1.0;
    public bool ShowConfidence { get; set; } = true;
    public bool Visible { get; set; } = true;
    /// <summary>
    /// Exclude the overlay from screen capture so it can never be OCR'd back in.
    /// Turn off if you want the overlay to appear in OBS/recordings (then keep it away from the RS region).
    /// </summary>
    public bool HideFromCapture { get; set; } = true;
}

/// <summary>
/// Chords like "Ctrl+Shift+M", key names like "PageUp", or a single symbol like "\" (resolved through the
/// keyboard layout, so it works on ABNT2 and US). Keys are only observed, never blocked: avoid keys the game uses.
/// </summary>
public sealed class HotkeySettings
{
    // In-game keys. "[" and "]" were the defaults until v0.1.4, but Star Citizen uses them to accept /
    // decline missions, so they moved to Page Up / Page Down (see AppSettings.Migrate).
    public string ToggleOverlay { get; set; } = "PageUp";
    /// <summary>Saves a full debug snapshot of this moment (screen, plate, OCR inputs, report) into debug_snapshots/.</summary>
    public string DebugSnapshot { get; set; } = "PageDown";
    /// <summary>Toggles overlay move mode (drag it anywhere, press again or double-click to lock).</summary>
    public string MoveOverlay { get; set; } = "\\";

    // Desktop / secondary actions.
    public string QuickScan { get; set; } = "Ctrl+Shift+S";
    public string Calibration { get; set; } = "Ctrl+Shift+C";
    public string PauseOcr { get; set; } = "Ctrl+Shift+P";
    public string DebugWindow { get; set; } = "Ctrl+Shift+D";
    /// <summary>Cycles scan context Auto → Space → Surface.</summary>
    public string CycleContext { get; set; } = "Ctrl+Shift+X";
}

public sealed class AppSettings
{
    /// <summary>Settings format; older files are upgraded once by <see cref="Migrate"/>.</summary>
    public int ConfigVersion { get; set; }
    public const int CurrentConfigVersion = 1;

    /// <summary>
    /// Upgrades settings saved by an older version. Returns true when something changed (save the file).
    /// v1: the overlay / snapshot hotkeys "[" and "]" collide with accepting / declining missions in
    /// Star Citizen; if they are still the old defaults they become Page Up / Page Down. Keys the user
    /// chose are left alone.
    /// </summary>
    public bool Migrate()
    {
        if (ConfigVersion >= CurrentConfigVersion) return false;
        if (Hotkeys.ToggleOverlay == "[") Hotkeys.ToggleOverlay = "PageUp";
        if (Hotkeys.DebugSnapshot == "]") Hotkeys.DebugSnapshot = "PageDown";
        ConfigVersion = CurrentConfigVersion;
        return true;
    }

    /// <summary>Folder under data/ holding materials.json ("default", "4.10.1", ...).</summary>
    public string GameVersion { get; set; } = "default";
    public string ActiveProfile { get; set; } = "default";
    /// <summary>Monitor index (0 = primary) the game runs on.</summary>
    public int MonitorIndex { get; set; } = 0;

    public ScanMode Mode { get; set; } = ScanMode.QuickScan;
    public int OcrIntervalMs { get; set; } = 200;
    public int VoteWindow { get; set; } = 5;
    public int VoteMinAgreement { get; set; } = 3;
    /// <summary>Agreeing frames enough for a clean read that a single signal explains exactly (faster, robust to flicker).</summary>
    public int VoteFastAgreement { get; set; } = 2;
    /// <summary>
    /// Save the ROI locally (diagnostics/missed/) when an RS indicator is visible but can't be read,
    /// or when a new stable RS is unknown. Used to improve recognition; nothing leaves the PC.
    /// </summary>
    public bool SaveMissedReads { get; set; } = true;
    public double HoldSeconds { get; set; } = 4;
    /// <summary>
    /// With scan context Auto, how long a context learned from a revealing signal (asteroid type → Space,
    /// FPS deposit → Surface) is remembered.
    /// </summary>
    public double ContextMemoryMinutes { get; set; } = 10;
    /// <summary>Skip OCR when the ROI pixels did not change since the last frame.</summary>
    public bool SkipUnchangedFrames { get; set; } = true;

    public bool DebugMode { get; set; } = false;
    /// <summary>Master switch for the global hotkeys.</summary>
    public bool HotkeysEnabled { get; set; } = true;
    /// <summary>
    /// Start scanning as soon as the app opens. Off (default): wait for the Start button, which runs a
    /// countdown so the user can switch to the game window first.
    /// </summary>
    public bool AutoStart { get; set; } = false;
    /// <summary>Countdown, in seconds, between clicking Start and the scanner actually starting (0 = none).</summary>
    public int StartDelaySeconds { get; set; } = 5;
    /// <summary>
    /// Minimize the control panel when scanning starts. The panel opens in the middle of the screen,
    /// exactly where the RS indicator appears, so leaving it there would hide the indicator.
    /// </summary>
    public bool MinimizePanelOnStart { get; set; } = true;
    /// <summary>Open the control panel when the app starts (otherwise it starts in the tray).</summary>
    public bool ShowPanelOnStartup { get; set; } = true;
    /// <summary>Control panel size chosen by the user (0 = fit the content, up to the screen height).</summary>
    public double PanelHeight { get; set; }
    /// <summary>The "Before you start" conditions window was shown and closed at least once.</summary>
    public bool RequirementsAcknowledged { get; set; }
    public double PanelWidth { get; set; }
    /// <summary>Layout the saved panel size belongs to; sizes saved for an older layout are ignored.</summary>
    public int PanelLayout { get; set; }
    public bool SoundAlerts { get; set; } = false;

    /// <summary>Beep (two-tone, distinct from the ore alert) when the stable RS is one of these numbers.</summary>
    public List<int> AlertRsValues { get; set; } = new();
    /// <summary>Also beep for multiples of <see cref="AlertRsValues"/> (3585 → 7170, 10755 …).</summary>
    public bool AlertRsMultiples { get; set; } = false;

    /// <summary>Open the pop-up that asks what an unknown RS is (stays until answered or dismissed).</summary>
    public bool AskForUnknownSignatures { get; set; } = true;
    /// <summary>RS values the user chose "never ask again" for.</summary>
    public List<int> IgnoredUnknownRs { get; set; } = new();
    public ContributionSettings Contribute { get; set; } = new();

    /// <summary>When TargetOnly is on, only these materials are announced ("TARGET FOUND").</summary>
    public List<string> TargetMaterials { get; set; } = new();
    public bool TargetOnly { get; set; } = false;

    public SignatureMatcherOptions Matcher { get; set; } = new();
    public OcrSettings Ocr { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
}

/// <summary>
/// Sending named signatures to the project's GitHub repository (contributions/ folder) so the shipped
/// database can be improved. Off unless the user turns it on; nothing is sent otherwise.
/// Uses the GitHub CLI ("gh") logged in on this PC — the app itself opens no network connection.
/// </summary>
public sealed class ContributionSettings
{
    public bool Enabled { get; set; } = false;
    public string Repository { get; set; } = "VorxenSC/StarCitizenMiningAssistant";
    public string Branch { get; set; } = "main";
    public string Folder { get; set; } = "contributions";
}
