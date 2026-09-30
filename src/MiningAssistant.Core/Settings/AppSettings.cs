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
    public OverlayPlacement Placement { get; set; } = OverlayPlacement.NextToSignal;

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
    public double XPercent { get; set; } = 0.78;
    public double YPercent { get; set; } = 0.12;
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

public sealed class HotkeySettings
{
    public string ToggleOverlay { get; set; } = "Ctrl+Shift+M";
    public string QuickScan { get; set; } = "Ctrl+Shift+S";
    public string Calibration { get; set; } = "Ctrl+Shift+C";
    public string PauseOcr { get; set; } = "Ctrl+Shift+P";
    public string SaveUnknown { get; set; } = "Ctrl+Shift+U";
    public string DebugWindow { get; set; } = "Ctrl+Shift+D";
    /// <summary>Cycles scan context Auto → Space → Surface.</summary>
    public string CycleContext { get; set; } = "Ctrl+Shift+X";
    /// <summary>Toggles overlay move mode (drag it anywhere, press again or double-click to lock).</summary>
    public string MoveOverlay { get; set; } = "Ctrl+Shift+O";
}

public sealed class AppSettings
{
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
    public bool SoundAlerts { get; set; } = false;

    /// <summary>When TargetOnly is on, only these materials are announced ("TARGET FOUND").</summary>
    public List<string> TargetMaterials { get; set; } = new();
    public bool TargetOnly { get; set; } = false;

    public SignatureMatcherOptions Matcher { get; set; } = new();
    public OcrSettings Ocr { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();
    public HotkeySettings Hotkeys { get; set; } = new();
}
