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
    /// <summary>Try grayscale first, then a binarized variant if no number was found.</summary>
    Auto,
    Grayscale,
    Binarize,
}

public sealed class OcrSettings
{
    /// <summary>BCP-47 tag of the Windows OCR language pack. Falls back to user profile languages if missing.</summary>
    public string Language { get; set; } = "en-US";
    /// <summary>ROI upscale factor before OCR. HUD digits are small; 2–4 helps Windows OCR a lot.</summary>
    public int Upscale { get; set; } = 3;
    public PreprocessMode Preprocess { get; set; } = PreprocessMode.Auto;
    public NumberNormalizerOptions Normalizer { get; set; } = new();
}

public sealed class OverlaySettings
{
    /// <summary>Top-left position as a fraction of the monitor.</summary>
    public double XPercent { get; set; } = 0.78;
    public double YPercent { get; set; } = 0.12;
    public double Opacity { get; set; } = 0.92;
    public double Scale { get; set; } = 1.0;
    public bool ShowConfidence { get; set; } = true;
    public bool Visible { get; set; } = true;
}

public sealed class HotkeySettings
{
    public string ToggleOverlay { get; set; } = "Ctrl+Shift+M";
    public string QuickScan { get; set; } = "Ctrl+Shift+S";
    public string Calibration { get; set; } = "Ctrl+Shift+C";
    public string PauseOcr { get; set; } = "Ctrl+Shift+P";
    public string SaveUnknown { get; set; } = "Ctrl+Shift+U";
    public string DebugWindow { get; set; } = "Ctrl+Shift+D";
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
    public double HoldSeconds { get; set; } = 4;
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
