using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MiningAssistant.App.Interop;

internal static class Win32
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x00000020;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_LAYERED = 0x00080000;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public const int WS_EX_TOPMOST = 0x00000008;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_SHOWWINDOW = 0x0040;

    public const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8;

    // Raw Input (keyboard, background sink) — passive receive-only API, see HotkeyManager.
    public const int WM_INPUT = 0x00FF;
    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RID_INPUT = 0x10000003;
    public const uint RIM_TYPEKEYBOARD = 1;
    public const ushort RI_KEY_BREAK = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWKEYBOARD
    {
        public ushort MakeCode;
        public ushort Flags;
        public ushort Reserved;
        public ushort VKey;
        public uint Message;
        public uint ExtraInformation;
    }

    /// <summary>Virtual key (low byte) and shift state (high byte: 1 Shift, 2 Ctrl, 4 Alt) that type a character in the current keyboard layout.</summary>
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "VkKeyScanW")]
    public static extern short VkKeyScan(char ch);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    /// <summary>Reads the virtual key and up/down state from a WM_INPUT lParam (keyboard only).</summary>
    public static bool TryReadKeyboard(IntPtr lParam, out uint vkey, out bool isUp)
    {
        vkey = 0;
        isUp = false;
        var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0) return false;

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(lParam, RID_INPUT, buffer, ref size, headerSize) != size) return false;
            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            if (header.dwType != RIM_TYPEKEYBOARD) return false;
            var kb = Marshal.PtrToStructure<RAWKEYBOARD>(buffer + (int)headerSize);
            vkey = kb.VKey;
            isUp = (kb.Flags & RI_KEY_BREAK) != 0;
            return true;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>Physical-pixel top-left of one of our own windows.</summary>
    public static (int x, int y) Position(Window w) =>
        GetWindowRect(Handle(w), out var r) ? (r.Left, r.Top) : (0, 0);

    /// <summary>Physical-pixel bounds of one of our own windows.</summary>
    public static (int x, int y, int width, int height) Bounds(Window w) =>
        GetWindowRect(Handle(w), out var r) ? (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top) : (0, 0, 0, 0);

    public static void RemoveExStyle(Window w, int flags)
    {
        var h = Handle(w);
        var ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex & ~(long)flags));
    }

    public static IntPtr Handle(Window w) => new WindowInteropHelper(w).EnsureHandle();

    public static void AddExStyle(Window w, int flags)
    {
        var h = Handle(w);
        var ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | (long)flags));
    }

    /// <summary>Overlay mode: click-through, never activated, hidden from Alt+Tab, always on top.</summary>
    public static void MakeOverlay(Window w)
    {
        AddExStyle(w, WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST);
    }

    /// <summary>Keeps a window out of screen captures (Win10 2004+) so the overlay is never OCR'd back in.</summary>
    public static void SetHiddenFromCapture(Window w, bool hidden) =>
        SetWindowDisplayAffinity(Handle(w), hidden ? WDA_EXCLUDEFROMCAPTURE : 0);

    /// <summary>Moves a window to physical-pixel coordinates (bypasses WPF DIP conversion).</summary>
    public static void MoveTo(Window w, int x, int y, int? width = null, int? height = null, bool topmost = true)
    {
        var flags = SWP_NOACTIVATE | (width is null ? SWP_NOSIZE : 0);
        SetWindowPos(Handle(w), topmost ? HWND_TOPMOST : IntPtr.Zero, x, y, width ?? 0, height ?? 0, flags);
    }

    public static void BringTopmost(Window w) =>
        SetWindowPos(Handle(w), HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
}
