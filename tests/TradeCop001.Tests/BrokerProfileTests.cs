using TradingCore.Configuration;

namespace TradeCop001.Tests;

/// <summary>
/// Broker profiles let one binary serve Deriv and Exness without changing code.
/// These tests lock down the settings that matter for switching venues safely.
/// </summary>
public class BrokerProfileTests
{
    [Fact]
    public void Catalog_ContainsDerivExnessHfmAndIbProfiles()
    {
        var keys = BrokerProfileCatalog.Keys;

        Assert.Contains("deriv-demo", keys);
        Assert.Contains("deriv-real", keys);
        Assert.Contains("exness-demo", keys);
        Assert.Contains("exness-real", keys);
        Assert.Contains("hfm-demo", keys);
        Assert.Contains("hfm-real", keys);
        Assert.Contains("ib-paper", keys);
    }

    [Fact]
    public void HfmProfiles_UseHfmServersAndPremiumSymbols()
    {
        var demo = BrokerProfileCatalog.Find("hfm-demo");
        var live = BrokerProfileCatalog.Find("hfm-real");

        Assert.NotNull(demo);
        Assert.NotNull(live);
        Assert.Equal("HFMarketsGlobal-Demo", demo!.Mt5Server);
        Assert.Equal("HFMarketsGlobal-Live1", live!.Mt5Server);
        Assert.Equal("mt5", demo.BrokerType);
        Assert.Equal("mt5", live.BrokerType);

        // Premium account: no suffix. Crypto uses the # prefix.
        Assert.Equal("#BTCUSD", demo.Symbols["BTCUSDT"]);
        Assert.Equal("#ETHUSD", demo.Symbols["ETHUSDT"]);
        Assert.Equal("XAUUSD", demo.Symbols["XAUUSD"]);
        Assert.Equal("EURUSD", demo.Symbols["EURUSD"]);
        Assert.Equal("GBPJPY", demo.Symbols["GBPJPY"]);
        Assert.Equal("USA30", demo.Symbols["US30"]);
        Assert.Equal("USA100", demo.Symbols["NAS100"]);
    }

    [Fact]
    public void ParseSymbolMap_ReadsPairsAndIgnoresMalformedEntries()
    {
        var map = BrokerProfileCatalog.ParseSymbolMap(
            "BTCUSDT=#BTCUSDr; XAUUSD=XAUUSDr, EURUSD=EURUSDr\nGBPUSD=;=oops");

        Assert.Equal(3, map.Count);
        Assert.Equal("#BTCUSDr", map["btcusdt"]);
        Assert.Equal("XAUUSDr", map["XAUUSD"]);
        Assert.Equal("EURUSDr", map["EURUSD"]);
        Assert.False(map.ContainsKey("GBPUSD"));
    }

    [Fact]
    public void ResolveSymbols_AppliesSuffixThenExplicitOverrides()
    {
        var config = new BrokerConfig();
        config.Settings["SymbolSuffix"] = "r";
        config.Settings["SymbolMap"] = "BTCUSDT=#BTCUSDr;XAUUSD=XAUUSDd";

        var map = BrokerProfileCatalog.ResolveSymbols(BrokerProfileCatalog.Find("hfm-demo"), config);

        // Pro/Zero-style suffix applies to profile defaults...
        Assert.Equal("EURUSDr", map["EURUSD"]);
        // ...but an explicit per-symbol override always wins.
        Assert.Equal("#BTCUSDr", map["BTCUSDT"]);
        Assert.Equal("XAUUSDd", map["XAUUSD"]);
    }

    [Fact]
    public void MinVolume_ReturnsKnownVenueMinimums()
    {
        var hfm = BrokerProfileCatalog.Find("hfm-demo");

        Assert.Equal(0.01m, BrokerProfileCatalog.MinVolume(hfm, "BTCUSDT"));
        Assert.Equal(0.01m, BrokerProfileCatalog.MinVolume(hfm, "XAUUSD"));
        Assert.Equal(0.01m, BrokerProfileCatalog.MinVolume(hfm, "EURUSD"));
        Assert.Equal(0.1m, BrokerProfileCatalog.MinVolume(hfm, "US30"));
        Assert.Null(BrokerProfileCatalog.MinVolume(hfm, "UNKNOWN"));
        Assert.Null(BrokerProfileCatalog.MinVolume(null, "BTCUSDT"));
    }

    [Fact]
    public void ResolveSymbols_WithoutProfileOnlyUsesExplicitOverrides()
    {
        var config = new BrokerConfig();
        config.Settings["SymbolMap"] = "BTCUSDT=BTCUSD";

        var map = BrokerProfileCatalog.ResolveSymbols(null, config);

        Assert.Single(map);
        Assert.Equal("BTCUSD", map["BTCUSDT"]);
        Assert.Empty(BrokerProfileCatalog.ResolveSymbols(null, new BrokerConfig()));
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
