using TradingCore.Configuration;

namespace TradeCop001.Tests;

/// <summary>
/// Broker profiles let one binary serve Deriv and Exness without changing code.
/// These tests lock down the settings that matter for switching venues safely.
/// </summary>
public class BrokerProfileTests
{
    [Fact]
    public void Catalog_ContainsDerivExnessAndIbProfiles()
    {
        var keys = BrokerProfileCatalog.Keys;

        Assert.Contains("deriv-demo", keys);
        Assert.Contains("deriv-real", keys);
        Assert.Contains("exness-demo", keys);
        Assert.Contains("exness-real", keys);
        Assert.Contains("ib-paper", keys);
    }

    [Fact]
    public void DerivProfile_IncludesSyntheticIndices()
    {
        var profile = BrokerProfileCatalog.Find("deriv-demo");

        Assert.NotNull(profile);
        Assert.Equal("Volatility 75 Index", profile!.Symbols["VOL75"]);
        Assert.Equal("BTCUSD", profile.Symbols["BTCUSDT"]);
        Assert.Equal("mt5", profile.BrokerType);
    }

    [Fact]
    public void ExnessProfile_UsesExnessServerAndForexSymbols()
    {
        var profile = BrokerProfileCatalog.Find("EXNESS-DEMO");

        Assert.NotNull(profile);
        Assert.Equal("Exness-MT5Trial9", profile!.Mt5Server);
        Assert.Equal("EURUSD", profile.Symbols["EURUSD"]);
        Assert.Equal("XAUUSD", profile.Symbols["XAUUSD"]);
    }

    [Fact]
    public void Apply_ChangesBrokerTypeAndServer()
    {
        var config = new BrokerConfig { Name = "venue", Type = "paper" };

        var profile = BrokerProfileCatalog.Apply(config, "deriv-demo");

        Assert.NotNull(profile);
        Assert.Equal("mt5", config.Type);
        Assert.Equal("Deriv-Demo", config.Get("Mt5Server"));
        Assert.Equal("deriv-demo", config.Get("Profile"));
    }

    [Fact]
    public void Apply_DoesNotOverwriteExplicitOperatorSettings()
    {
        var config = new BrokerConfig();
        config.Settings["Mt5Server"] = "My-Custom-Server";

        BrokerProfileCatalog.Apply(config, "exness-demo");

        Assert.Equal("My-Custom-Server", config.Get("Mt5Server"));
    }

    [Fact]
    public void Apply_WithUnknownOrEmptyProfile_IsANoOp()
    {
        var config = new BrokerConfig { Type = "paper" };

        Assert.Null(BrokerProfileCatalog.Apply(config, ""));
        Assert.Null(BrokerProfileCatalog.Apply(config, "does-not-exist"));
        Assert.Equal("paper", config.Type);
        Assert.Empty(config.Settings);
    }
}
