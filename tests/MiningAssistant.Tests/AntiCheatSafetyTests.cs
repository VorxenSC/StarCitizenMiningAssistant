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
        // ...and not through a helper program either: no driving command-line tools (e.g. the GitHub CLI)
        // and reading their output. Opening a file, folder or link with the user's own apps is fine.
        "gh.exe", "GitHub CLI", "RedirectStandardOutput", "RedirectStandardInput", ".ArgumentList",
    };

    /// <summary>The only native functions the app is allowed to import, all from user32.dll.</summary>
    private static readonly HashSet<string> AllowedNative = new()
    {
        "EnumDisplayMonitors", "GetMonitorInfo", "SetProcessDpiAwarenessContext",
        "GetWindowLongPtrW", "SetWindowLongPtrW", "SetWindowPos", "SetWindowDisplayAffinity", "GetWindowRect",
        // Hotkeys: passive keyboard Raw Input sink (receive-only; no hook, keys are not blocked or altered).
        "RegisterRawInputDevices", "GetRawInputData",
        // Keyboard layout lookup so symbol hotkeys like "/" work on ABNT2 and US layouts.
        "VkKeyScanW",
    };

    private const string UpdaterFile = "Updater.cs";

    /// <summary>
    /// Updates are the app's only network access: one file, HTTPS GETs to github.com only, and every
    /// download is checked against the signed manifest before it is installed.
    /// </summary>
    [Fact]
    public void Network_is_only_the_signed_github_updater()
    {
        var files = SourceFiles().ToList();
        var updater = Assert.Single(files, f => Path.GetFileName(f) == UpdaterFile);
        var text = File.ReadAllText(updater);

        var hosts = Regex.Matches(text, @"https?://([^/""\s{]+)").Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(hosts);
        Assert.All(hosts, h => Assert.Equal("github.com", h));
        Assert.DoesNotContain("http://", text);                       // HTTPS only
        Assert.DoesNotContain("PostAsync", text);                     // nothing is uploaded
        Assert.DoesNotContain("PutAsync", text);
        Assert.Contains("UpdateVerifier.IsSignatureValid", text);     // manifest must be signed
        Assert.Contains("Sha256", text);                              // download must match it

        foreach (var f in files.Where(f => f != updater))
            Assert.DoesNotContain("System.Net.Http", File.ReadAllText(f));
    }

    [Fact]
    public void Raw_input_is_registered_passively()
    {
        // Receive-only in the background. RIDEV_NOLEGACY / RIDEV_NOHOTKEYS / RIDEV_CAPTUREMOUSE would
        // change how *other* apps get input; they must never be used.
        var all = string.Join("\n", SourceFiles().Select(File.ReadAllText));
        Assert.Contains("RIDEV_INPUTSINK", all);
        foreach (var flag in new[] { "RIDEV_NOLEGACY", "RIDEV_NOHOTKEYS", "RIDEV_CAPTUREMOUSE", "RIDEV_APPKEYS" })
            Assert.DoesNotContain(flag, all);
    }

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
            {
                // The one sanctioned network use: opt-in automatic updates, checked by the test below.
                if (api == "HttpClient" && Path.GetFileName(file) == UpdaterFile) continue;
                if (text.Contains(api, StringComparison.Ordinal))
                    hits.Add($"{Path.GetFileName(file)}: {api}");
            }
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
