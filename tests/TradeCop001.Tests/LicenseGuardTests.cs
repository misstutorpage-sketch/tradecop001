using TradeCop001.Licensing;

namespace TradeCop001.Tests;

public class LicenseGuardTests
{
    private static License ValidLicense(string device = "device123")
        => new()
        {
            LicenseId = "TEST001",
            Licensee = "Test",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            MaxDevices = 1,
            DeviceFingerprints = new List<string> { device },
            Features = new List<string> { LicenseFeatures.Paper, LicenseFeatures.ControlLocal }
        };

    [Fact]
    public void Validate_AcceptsMatchingDevice()
    {
        var license = ValidLicense("device123");

        var ex = Record.Exception(() => LicenseGuard.Validate(license, "DEVICE123"));

        Assert.Null(ex); // case-insensitive match
    }

    [Fact]
    public void Validate_RejectsUnknownDevice()
    {
        var license = ValidLicense("device123");

        var ex = Assert.Throws<InvalidOperationException>(() => LicenseGuard.Validate(license, "otherdevice"));

        Assert.Contains("not licensed", ex.Message);
        Assert.Contains("otherdevice", ex.Message);
    }

    [Fact]
    public void Validate_RejectsExpiredLicense()
    {
        var license = ValidLicense();
        license.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1);

        Assert.Throws<InvalidOperationException>(() => LicenseGuard.Validate(license, "device123"));
    }

    [Fact]
    public void Validate_RejectsLicenseWithNoDevices()
    {
        var license = ValidLicense();
        license.DeviceFingerprints.Clear();

        Assert.Throws<InvalidOperationException>(() => LicenseGuard.Validate(license, "device123"));
    }

    [Fact]
    public void Validate_RejectsMoreDevicesThanMaxDevices()
    {
        var license = ValidLicense();
        license.MaxDevices = 1;
        license.DeviceFingerprints.Add("device456");

        Assert.Throws<InvalidOperationException>(() => LicenseGuard.Validate(license, "device123"));
    }

    [Fact]
    public void EnsureFeature_AcceptsPresentFeature()
    {
        var license = ValidLicense();
        license.Features.Add("broker.crypto");

        var ex = Record.Exception(() => LicenseGuard.EnsureFeature(license, "broker.crypto"));

        Assert.Null(ex);
    }

    [Fact]
    public void EnsureFeature_RejectsMissingFeature()
    {
        var license = ValidLicense();

        Assert.Throws<InvalidOperationException>(() => LicenseGuard.EnsureFeature(license, "broker.crypto"));
    }

    [Theory]
    [InlineData("paper", LicenseFeatures.Paper)]
    [InlineData("crypto", LicenseFeatures.Crypto)]
    [InlineData("ib", LicenseFeatures.Ib)]
    [InlineData("mt5", LicenseFeatures.Mt5)]
    public void FeatureForBroker_MapsCorrectly(string broker, string expected)
        => Assert.Equal(expected, LicenseGuard.FeatureForBroker(broker));

    [Fact]
    public void FeatureForBroker_RejectsUnknownBroker()
        => Assert.Throws<InvalidOperationException>(() => LicenseGuard.FeatureForBroker("unknown"));
}
