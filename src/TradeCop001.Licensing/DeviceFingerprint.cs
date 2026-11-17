using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace TradeCop001.Licensing;

/// <summary>
/// Produces a stable, opaque identifier for the machine running tradecop001.
/// Nothing is transmitted anywhere — the fingerprint is shown to the operator
/// and pasted into a license you sign yourself.
/// </summary>
public static class DeviceFingerprint
{
    public static string Compute()
    {
        var parts = new List<string>
        {
            ReadRegistry(@"SOFTWARE\Microsoft\Cryptography", "MachineGuid"),
            Environment.MachineName,
            Environment.OSVersion.VersionString,
            ReadRegistry(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString"),
            PrimaryMac()
        }
        .Where(p => !string.IsNullOrWhiteSpace(p));

        var raw = string.Join('|', parts);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    private static string PrimaryMac()
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n =>
                    n.OperationalStatus == OperationalStatus.Up &&
                    n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    !string.IsNullOrWhiteSpace(n.GetPhysicalAddress().ToString()) &&
                    n.GetPhysicalAddress().ToString() != "000000000000");

            return nic?.GetPhysicalAddress().ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static string ReadRegistry(string key, string value)
    {
        if (!OperatingSystem.IsWindows())
            return "";

        try
        {
            using var reg = Registry.LocalMachine.OpenSubKey(key);
            return reg?.GetValue(value)?.ToString() ?? "";
        }
        catch
        {
            return "";
        }
    }
}
