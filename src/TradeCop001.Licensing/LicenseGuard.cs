using System.Text.Json;

namespace TradeCop001.Licensing;

/// <summary>
/// Verifies the license signature and enforces device/expiry/feature limits.
/// If validation fails, the app refuses to start — no silent fallback.
/// </summary>
public sealed class LicenseGuard
{
    private readonly string _publicKey;
    private readonly string _licensePath;
    private readonly bool _requireLicense;

    public LicenseGuard(string publicKey, string licensePath, bool requireLicense = true)
    {
        _publicKey = publicKey;
        _licensePath = licensePath;
        _requireLicense = requireLicense;
    }

    public License LoadAndValidate()
    {
        if (_requireLicense && !LicensingPublicKey.IsConfigured)
            throw new InvalidOperationException(
                "Licensing is enabled but no public key is baked in. " +
                "Run LicenseTool keygen and paste the public key into LicensingPublicKey.cs.");

        var json = Environment.GetEnvironmentVariable("TRADECOP_LICENSE");
        if (string.IsNullOrWhiteSpace(json) && File.Exists(_licensePath))
            json = File.ReadAllText(_licensePath);

        if (string.IsNullOrWhiteSpace(json))
        {
            if (!_requireLicense)
                return new License { Licensee = "LOCAL DEV", Notes = "Licensing disabled" };

            throw new InvalidOperationException(
                "No license found. Place a signed license.json beside tradecop001.exe " +
                "or set the TRADECOP_LICENSE environment variable.");
        }

        SignedEnvelope envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<SignedEnvelope>(json)
                       ?? throw new InvalidOperationException("Empty license envelope.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException("license.json is not a valid signed license file.", ex);
        }

        var license = Signing.Verify<License>(envelope, _publicKey);
        Validate(license, DeviceFingerprint.Compute());
        return license;
    }

    public static void Validate(License license, string deviceFingerprint)
    {
        if (license.ExpiresAt < DateTimeOffset.UtcNow)
            throw new InvalidOperationException($"License expired on {license.ExpiresAt:u}.");

        if (license.DeviceFingerprints.Count == 0)
            throw new InvalidOperationException("License contains no approved devices.");

        if (license.DeviceFingerprints.Count > Math.Max(1, license.MaxDevices))
            throw new InvalidOperationException("License lists more devices than MaxDevices allows.");

        if (!license.DeviceFingerprints.Contains(deviceFingerprint, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"This device is not licensed. Current fingerprint: {deviceFingerprint}. " +
                "Send that fingerprint to the license issuer.");
    }

    public static void EnsureFeature(License license, string feature)
    {
        if (!license.Features.Contains(feature, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"License does not include the '{feature}' feature.");
    }

    public static string FeatureForBroker(string brokerType) => brokerType.ToLowerInvariant() switch
    {
        "paper" => LicenseFeatures.Paper,
        "crypto" => LicenseFeatures.Crypto,
        "ib" => LicenseFeatures.Ib,
        "mt5" => LicenseFeatures.Mt5,
        _ => throw new InvalidOperationException($"No license feature mapped to broker type '{brokerType}'.")
    };
}
