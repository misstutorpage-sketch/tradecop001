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

        // HFM (HF Markets) defaults to the Premium account, which uses no symbol
        // suffix. Other account types append a suffix (Pro=r, Zero=b, Cent=c,
        // KATANA/InfinityX=x, Premium Pro=.V). Use Broker:Settings:SymbolSuffix
        // or an explicit SymbolMap for those accounts.
        new BrokerProfile(
            "hfm-demo", "HFM (HF Markets) MT5 Demo", "mt5", "HFMarketsGlobal-Demo",
            HfmSymbols(),
            HfmContracts()),

        new BrokerProfile(
            "hfm-real", "HFM (HF Markets) MT5 Live", "mt5", "HFMarketsGlobal-Live1",
            HfmSymbols(),
            HfmContracts()),

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

    /// <summary>
    /// HFM Premium-style names: forex, metals, crypto and indices, no suffix.
    /// Canonical names stay broker-agnostic on the strategy side.
    /// </summary>
    private static IReadOnlyDictionary<string, string> HfmSymbols() =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["EURUSD"] = "EURUSD",
            ["GBPUSD"] = "GBPUSD",
            ["USDJPY"] = "USDJPY",
            ["AUDUSD"] = "AUDUSD",
            ["USDCAD"] = "USDCAD",
            ["USDCHF"] = "USDCHF",
            ["NZDUSD"] = "NZDUSD",
            ["EURGBP"] = "EURGBP",
            ["EURJPY"] = "EURJPY",
            ["GBPJPY"] = "GBPJPY",
            ["BTCUSDT"] = "#BTCUSD",
            ["ETHUSDT"] = "#ETHUSD",
            ["XAUUSD"] = "XAUUSD",
            ["XAGUSD"] = "XAGUSD",
            ["US30"] = "USA30",
            ["NAS100"] = "USA100",
            ["SPX500"] = "USA500",
            ["GER40"] = "GER40",
            ["UK100"] = "UK100",
            ["JPN225"] = "JPN225"
        };

    private static IReadOnlyDictionary<string, ContractSpec> HfmContracts() =>
        new Dictionary<string, ContractSpec>(StringComparer.OrdinalIgnoreCase)
        {
            ["#BTCUSD"] = new(0.01m, 0.01m, 100m, 0.01m),
            ["#ETHUSD"] = new(0.01m, 0.01m, 500m, 0.01m),
            ["EURUSD"] = new(0.00001m, 0.01m, 100m, 0.01m),
            ["GBPUSD"] = new(0.00001m, 0.01m, 100m, 0.01m),
            ["USDJPY"] = new(0.001m, 0.01m, 100m, 0.01m),
            ["AUDUSD"] = new(0.00001m, 0.01m, 100m, 0.01m),
            ["USDCAD"] = new(0.00001m, 0.01m, 100m, 0.01m),
            ["USDCHF"] = new(0.00001m, 0.01m, 100m, 0.01m),
            ["NZDUSD"] = new(0.00001m, 0.01m, 100m, 0.01m),
            ["EURGBP"] = new(0.00001m, 0.01m, 100m, 0.01m),
            ["EURJPY"] = new(0.001m, 0.01m, 100m, 0.01m),
            ["GBPJPY"] = new(0.001m, 0.01m, 100m, 0.01m),
            ["XAUUSD"] = new(0.01m, 0.01m, 100m, 0.01m),
            ["XAGUSD"] = new(0.001m, 0.01m, 100m, 0.01m),
            ["USA30"] = new(0.1m, 0.1m, 100m, 0.1m),
            ["USA100"] = new(0.1m, 0.1m, 100m, 0.1m),
            ["USA500"] = new(0.1m, 0.1m, 100m, 0.1m),
            ["GER40"] = new(0.1m, 0.1m, 100m, 0.1m),
            ["UK100"] = new(0.1m, 0.1m, 100m, 0.1m),
            ["JPN225"] = new(0.1m, 0.1m, 100m, 0.1m)
        };

    /// <summary>
    /// Smallest order quantity the venue accepts for a canonical symbol, when the
    /// profile knows the contract. Returns null for unknown instruments.
    /// </summary>
    public static decimal? MinVolume(BrokerProfile? profile, string canonicalSymbol)
    {
        if (profile is null || string.IsNullOrWhiteSpace(canonicalSymbol))
            return null;

        return profile.Symbols.TryGetValue(canonicalSymbol, out var venue)
               && profile.Contracts.TryGetValue(venue, out var spec)
            ? spec.MinVolume
            : null;
    }

    /// <summary>
    /// Parses "BTCUSDT=#BTCUSDr;XAUUSD=XAUUSDr" (semicolon, comma or newline
    /// separated). Malformed pairs are ignored so a typo cannot silently map the
    /// wrong instrument.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseSymbolMap(string? spec)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(spec))
            return map;

        foreach (var pair in spec.Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
                continue;
            map[parts[0]] = parts[1];
        }

        return map;
    }

    /// <summary>
    /// Builds the effective canonical-to-venue symbol map: profile defaults,
    /// optional suffix for the operator's HFM account type, then explicit
    /// per-symbol overrides (which always win).
    /// </summary>
    public static IReadOnlyDictionary<string, string> ResolveSymbols(BrokerProfile? profile, BrokerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (profile is not null)
        {
            foreach (var pair in profile.Symbols)
                map[pair.Key] = pair.Value;
        }

        var suffix = config.Get("SymbolSuffix").Trim();
        if (suffix.Length > 0)
        {
            foreach (var key in map.Keys.ToArray())
                map[key] += suffix;
        }

        foreach (var pair in ParseSymbolMap(config.Get("SymbolMap")))
            map[pair.Key] = pair.Value;

        return map;
    }

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
