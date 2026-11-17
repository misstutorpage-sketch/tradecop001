namespace TradeCop001.Licensing;

public static class LicenseFeatures
{
    public const string Paper = "broker.paper";
    public const string Crypto = "broker.crypto";
    public const string Ib = "broker.ib";
    public const string Mt5 = "broker.mt5";
    public const string ControlLocal = "control.local";
    public const string ControlRemote = "control.remote";
}

/// <summary>
/// A signed license. The device list is what limits access to the machines
/// you explicitly approved. Keep the private signing key offline.
/// </summary>
public sealed class License
{
    public string LicenseId { get; set; } = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    public string Licensee { get; set; } = "";
    public string Issuer { get; set; } = "tradecop001";
    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddYears(1);
    public List<string> DeviceFingerprints { get; set; } = new();
    public List<string> Features { get; set; } = new();
    public int MaxDevices { get; set; } = 1;
    public string Notes { get; set; } = "";
}
