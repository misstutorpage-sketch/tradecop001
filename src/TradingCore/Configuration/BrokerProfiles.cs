namespace TradingCore.Configuration;

/// <summary>Contract conventions used for sizing and validation on a venue.</summary>
public sealed record ContractSpec(
    decimal TickSize,
    decimal MinVolume,
    decimal MaxVolume,
    decimal VolumeStep);

/// <summary>
/// A named, broker-specific setup. Canonical symbols stay the same everywhere in
/// the app; the profile maps them to the symbol name the venue actually uses.
/// </summary>
public sealed record BrokerProfile(
    string Key,
    string DisplayName,
    string BrokerType,
    string Mt5Server,
    IReadOnlyDictionary<string, string> Symbols,
    IReadOnlyDictionary<string, ContractSpec> Contracts);

public static class BrokerProfileCatalog
{
    private static readonly IReadOnlyList<BrokerProfile> Profiles = new[]
    {
        new BrokerProfile(
            "deriv-demo", "Deriv MT5 Demo", "mt5", "Deriv-Demo",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSDT"] = "BTCUSD",
                ["ETHUSDT"] = "ETHUSD",
                ["EURUSD"] = "EURUSD",
                ["GBPUSD"] = "GBPUSD",
                ["USDJPY"] = "USDJPY",
                ["XAUUSD"] = "XAUUSD",
                ["VOL25"] = "Volatility 25 Index",
                ["VOL50"] = "Volatility 50 Index",
                ["VOL75"] = "Volatility 75 Index",
                ["VOL100"] = "Volatility 100 Index"
            },
            new Dictionary<string, ContractSpec>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSD"] = new(0.01m, 0.01m, 100m, 0.01m),
                ["EURUSD"] = new(0.00001m, 0.01m, 100m, 0.01m),
                ["Volatility 75 Index"] = new(0.01m, 0.01m, 100m, 0.01m)
            }),

        new BrokerProfile(
            "deriv-real", "Deriv MT5 Real", "mt5", "Deriv-Real",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSDT"] = "BTCUSD", ["ETHUSDT"] = "ETHUSD", ["EURUSD"] = "EURUSD",
                ["GBPUSD"] = "GBPUSD", ["USDJPY"] = "USDJPY", ["XAUUSD"] = "XAUUSD",
                ["VOL25"] = "Volatility 25 Index", ["VOL50"] = "Volatility 50 Index",
                ["VOL75"] = "Volatility 75 Index", ["VOL100"] = "Volatility 100 Index"
            },
            new Dictionary<string, ContractSpec>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSD"] = new(0.01m, 0.01m, 100m, 0.01m),
                ["EURUSD"] = new(0.00001m, 0.01m, 100m, 0.01m),
                ["Volatility 75 Index"] = new(0.01m, 0.01m, 100m, 0.01m)
            }),

        new BrokerProfile(
            "exness-demo", "Exness MT5 Demo", "mt5", "Exness-MT5Trial9",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSDT"] = "BTCUSD", ["ETHUSDT"] = "ETHUSD", ["EURUSD"] = "EURUSD",
                ["GBPUSD"] = "GBPUSD", ["USDJPY"] = "USDJPY", ["XAUUSD"] = "XAUUSD", ["US30"] = "US30"
            },
            new Dictionary<string, ContractSpec>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSD"] = new(0.01m, 0.01m, 100m, 0.01m),
                ["EURUSD"] = new(0.00001m, 0.01m, 100m, 0.01m),
                ["XAUUSD"] = new(0.01m, 0.01m, 100m, 0.01m)
            }),

        new BrokerProfile(
            "exness-real", "Exness MT5 Real", "mt5", "Exness-MT5Real",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSDT"] = "BTCUSD", ["ETHUSDT"] = "ETHUSD", ["EURUSD"] = "EURUSD",
                ["GBPUSD"] = "GBPUSD", ["USDJPY"] = "USDJPY", ["XAUUSD"] = "XAUUSD", ["US30"] = "US30"
            },
            new Dictionary<string, ContractSpec>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSD"] = new(0.01m, 0.01m, 100m, 0.01m),
                ["EURUSD"] = new(0.00001m, 0.01m, 100m, 0.01m),
                ["XAUUSD"] = new(0.01m, 0.01m, 100m, 0.01m)
            }),

        new BrokerProfile(
            "ib-paper", "Interactive Brokers Paper", "ib", "",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["BTCUSDT"] = "BTC-USD", ["EURUSD"] = "EUR.USD", ["AAPL"] = "AAPL"
            },
            new Dictionary<string, ContractSpec>(StringComparer.OrdinalIgnoreCase)
            {
                ["AAPL"] = new(0.01m, 1m, 10000m, 1m)
            })
    };

    public static IReadOnlyList<BrokerProfile> All => Profiles;

    public static IReadOnlyCollection<string> Keys => Profiles.Select(p => p.Key).ToArray();

    public static BrokerProfile? Find(string? key)
        => key is null ? null : Profiles.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Applies a profile to a config, without overwriting explicit operator overrides.</summary>
    public static BrokerProfile? Apply(BrokerConfig config, string? key)
    {
        var profile = Find(key);
        if (profile is null)
            return null;

        config.Type = profile.BrokerType;
        config.Settings.TryAdd("Mt5Server", profile.Mt5Server);
        config.Settings.TryAdd("Profile", profile.Key);
        return profile;
    }
}
