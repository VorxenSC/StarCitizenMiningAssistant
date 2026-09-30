using System.Text.RegularExpressions;

namespace MiningAssistant.Tests;

/// <summary>
/// Guard rail: the app must stay fully external to Star Citizen (Easy Anti-Cheat).
/// It may only read the composed desktop image and manage its own windows. These tests fail the
/// build if any source file starts using APIs that open, read, write, hook or drive another
/// process or send synthetic input.
/// </summary>
public class AntiCheatSafetyTests
{
    private static readonly string[] Forbidden =
    {
        // process access / memory
        "OpenProcess", "ReadProcessMemory", "WriteProcessMemory", "VirtualAllocEx", "VirtualProtectEx",
        "CreateRemoteThread", "NtReadVirtualMemory", "NtOpenProcess", "DuplicateHandle", "MiniDumpWriteDump",
        "Process.GetProcesses", "Process.GetProcessesByName", "GetProcessById", ".MainModule", ".Modules",
        // injection / hooks
        "SetWindowsHookEx", "LoadLibrary", "GetProcAddress", "QueueUserAPC", "SetThreadContext",
        // synthetic input / driving the game window
        "SendInput", "keybd_event", "mouse_event", "SendKeys", "PostMessage", "SendMessage",
        "SetForegroundWindow", "FindWindow", "AttachThreadInput",
        // reading the game window directly instead of the desktop
        "PrintWindow", "GetWindowDC",
        // network (all processing must stay local)
        "HttpClient", "WebClient", "TcpClient", "UdpClient", "Socket(",
    };

    /// <summary>The only native functions the app is allowed to import, all from user32.dll.</summary>
    private static readonly HashSet<string> AllowedNative = new()
    {
        "EnumDisplayMonitors", "GetMonitorInfo", "SetProcessDpiAwarenessContext",
        "GetWindowLongPtrW", "SetWindowLongPtrW", "SetWindowPos", "SetWindowDisplayAffinity",
        "RegisterHotKey", "UnregisterHotKey",
    };

    private static IEnumerable<string> SourceFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MiningAssistant.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Directory.EnumerateFiles(Path.Combine(dir!.FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));
    }

    [Fact]
    public void No_process_memory_injection_input_or_network_apis()
    {
        var hits = new List<string>();
        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            foreach (var api in Forbidden)
                if (text.Contains(api, StringComparison.Ordinal))
                    hits.Add($"{Path.GetFileName(file)}: {api}");
        }
        Assert.True(hits.Count == 0, "Forbidden APIs found:\n" + string.Join("\n", hits));
    }

    [Fact]
    public void Only_whitelisted_user32_functions_are_imported()
    {
        var import = new Regex(@"\[(DllImport|LibraryImport)\(""(?<dll>[^""]+)""(?<rest>[^\]]*)\]\s*(public|internal|private)?\s*static\s+extern\s+\S+\s+(?<name>\w+)", RegexOptions.Multiline);
        var entry = new Regex(@"EntryPoint\s*=\s*""(?<ep>\w+)""");
        var found = new List<string>();
        foreach (var file in SourceFiles())
            foreach (Match m in import.Matches(File.ReadAllText(file)))
            {
                var dll = m.Groups["dll"].Value.ToLowerInvariant();
                var ep = entry.Match(m.Groups["rest"].Value);
                var name = ep.Success ? ep.Groups["ep"].Value : m.Groups["name"].Value;
                found.Add(name);
                Assert.True(dll == "user32.dll", $"{Path.GetFileName(file)} imports from {dll}");
                Assert.True(AllowedNative.Contains(name), $"{Path.GetFileName(file)} imports non-whitelisted {name}");
            }
        Assert.NotEmpty(found); // sanity: the scan actually sees the imports
    }
}
