using Microsoft.Win32;
using MiningAssistant.Core.Licensing;

namespace MiningAssistant.App.Services;

/// <summary>
/// This PC's code for license keys (<see cref="PcCode"/>). Source: the Windows installation id
/// (HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid), read-only, through the managed registry API.
/// Only its hash is ever shown; nothing is sent anywhere.
/// </summary>
public static class MachineIdentity
{
    private static string? _code;

    public static string Code => _code ??= PcCode.FromMachineId(MachineId());

    private static string MachineId()
    {
        try
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hklm.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is string guid && guid.Length > 0) return guid;
        }
        catch
        {
            // fall through
        }
        return "NAME|" + Environment.MachineName; // should not happen on Windows 10/11
    }
}
