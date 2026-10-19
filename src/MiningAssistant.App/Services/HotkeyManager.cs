using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using MiningAssistant.App.Interop;

namespace MiningAssistant.App.Services;

/// <summary>
/// Global hotkeys that keep working while Star Citizen has focus.
///
/// RegisterHotKey is not enough: games that register Raw Input with the "no hotkeys" flag
/// (Star Citizen does) disable application hotkeys while focused. Instead we register for keyboard Raw Input with
/// RIDEV_INPUTSINK, the documented way for a background window to *receive* key events.
/// It is passive: no hook, nothing injected, keys are neither blocked nor altered, and the game
/// process is never touched. Because keys are not consumed, pick chords the game does not use.
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C;

    private readonly HwndSource _source;
    private readonly List<(uint mods, uint vk, Action action)> _chords = new();
    private readonly HashSet<uint> _down = new();
    /// <summary>Last event (down or auto-repeat) per held key; see <see cref="ForgetStaleKeys"/>.</summary>
    private readonly Dictionary<uint, DateTime> _lastSeen = new();
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(2);

    public List<string> Failures { get; } = new();
    public bool RawInputActive { get; }

    /// <summary>When false, keys are still counted (diagnostics) but no hotkey fires.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Diagnostics: key presses received and when. If this does not grow while pressing keys in the
    /// game, Windows is not delivering them (e.g. the game runs as administrator and this app does not).
    /// </summary>
    public int KeysReceived { get; private set; }
    public DateTime? LastKeyAt { get; private set; }
    public DateTime? LastHotkeyAt { get; private set; }

    public HotkeyManager()
    {
        // Hidden (never shown) top-level window: raw input sink target.
        var p = new HwndSourceParameters("MiningAssistantHotkeys") { Width = 0, Height = 0, WindowStyle = 0 };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);

        var device = new Win32.RAWINPUTDEVICE
        {
            usUsagePage = 0x01, // generic desktop
            usUsage = 0x06,     // keyboard
            dwFlags = Win32.RIDEV_INPUTSINK,
            hwndTarget = _source.Handle,
        };
        RawInputActive = Win32.RegisterRawInputDevices(new[] { device }, 1, (uint)Marshal.SizeOf<Win32.RAWINPUTDEVICE>());
        if (!RawInputActive) Failures.Add($"Raw input registration failed (error {Marshal.GetLastWin32Error()})");
    }

    /// <summary>Registers e.g. "Ctrl+Shift+M". Returns false (and records why) if the chord is invalid.</summary>
    public bool Register(string chord, Action action)
    {
        if (string.IsNullOrWhiteSpace(chord)) return false;
        if (!TryParse(chord, out var mods, out var vk))
        {
            Failures.Add($"Invalid hotkey '{chord}'");
            return false;
        }
        _chords.Add((mods, vk, action));
        return true;
    }

    public void UnregisterAll()
    {
        _chords.Clear();
        Failures.RemoveAll(f => f.StartsWith("Invalid"));
    }

    public static bool TryParse(string chord, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        foreach (var raw in chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= Win32.MOD_CONTROL; break;
                case "shift": modifiers |= Win32.MOD_SHIFT; break;
                case "alt": modifiers |= Win32.MOD_ALT; break;
                case "win": modifiers |= Win32.MOD_WIN; break;
                default:
                    // Single symbol ("/", "\", "["): resolve through the active keyboard layout, so the
                    // key that types that character is used (ABNT2 and US place them differently).
                    if (raw.Length == 1 && !char.IsLetterOrDigit(raw[0]))
                    {
                        var scan = Win32.VkKeyScan(raw[0]);
                        if (scan == -1) return false;
                        vk = (uint)(scan & 0xFF);
                        var shift = (scan >> 8) & 0xFF;
                        if ((shift & 1) != 0) modifiers |= Win32.MOD_SHIFT;
                        if ((shift & 2) != 0) modifiers |= Win32.MOD_CONTROL;
                        if ((shift & 4) != 0) modifiers |= Win32.MOD_ALT;
                        break;
                    }
                    var name = raw.Length == 1 && char.IsDigit(raw[0]) ? "D" + raw : raw;
                    if (!Enum.TryParse<Key>(name, true, out var key)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0;
    }

    private uint CurrentModifiers()
    {
        uint m = 0;
        if (_down.Contains(VK_CONTROL)) m |= Win32.MOD_CONTROL;
        if (_down.Contains(VK_SHIFT)) m |= Win32.MOD_SHIFT;
        if (_down.Contains(VK_MENU)) m |= Win32.MOD_ALT;
        if (_down.Contains(VK_LWIN) || _down.Contains(VK_RWIN)) m |= Win32.MOD_WIN;
        return m;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != Win32.WM_INPUT || !Win32.TryReadKeyboard(lParam, out var vkey, out var isUp)) return IntPtr.Zero;
        if (vkey == 0 || vkey == 0xFF) return IntPtr.Zero; // fake/overrun keys

        var now = DateTime.UtcNow;
        if (isUp)
        {
            _down.Remove(vkey);
            _lastSeen.Remove(vkey);
            return IntPtr.Zero;
        }

        ForgetStaleKeys(now);
        // Fire on the up→down transition only (ignore auto-repeat while held).
        var isNew = _down.Add(vkey);
        _lastSeen[vkey] = now;
        if (!isNew) return IntPtr.Zero;
        KeysReceived++;
        LastKeyAt = DateTime.Now;

        var mods = CurrentModifiers();
        foreach (var (m, vk, action) in _chords)
            if (Enabled && vk == vkey && m == mods)
            {
                LastHotkeyAt = DateTime.Now;
                action();
            }

        return IntPtr.Zero; // let DefWindowProc clean up WM_INPUT
    }

    /// <summary>
    /// A key-up can be lost (Win+L, a UAC prompt, switching user): the key would then look held forever and,
    /// for Ctrl/Alt/Shift/Win, block every hotkey without modifiers. A key really held down keeps sending
    /// auto-repeat events several times per second, so one silent for 2 s is treated as released.
    /// </summary>
    private void ForgetStaleKeys(DateTime now)
    {
        foreach (var (key, seen) in _lastSeen.ToList())
            if (now - seen > StaleAfter)
            {
                _down.Remove(key);
                _lastSeen.Remove(key);
            }
    }

    public void Dispose()
    {
        _chords.Clear();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
