using TradingCore.Abstractions;
using TradingCore.Models;

namespace TradingCore.Configuration;

/// <summary>A broker type key, e.g. "paper", "crypto", "ib", "mt5".</summary>
public sealed class BrokerConfig
{
    public string Name { get; set; } = "default";
    public string Type { get; set; } = "paper";
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public string Get(string key, string fallback = "")
        => Settings.TryGetValue(key, out var value) ? value : fallback;

    public BrokerCredentials ToCredentials() => new(
        ApiKey: Get("ApiKey"),
        ApiSecret: Get("ApiSecret"),
        Passphrase: Settings.TryGetValue("Passphrase", out var p) ? p : null,
        AccountId: Settings.TryGetValue("AccountId", out var a) ? a : null);
}

/// <summary>Each broker plugin implements one of these so it can register itself.</summary>
public interface IBrokerAdapterFactory
{
    string Type { get; }
    IBrokerAdapter Create(BrokerConfig config);
}

/// <summary>
/// The runtime switch. Change the "Broker:Type" setting and the entire app
/// talks to a different venue without a single strategy code change.
/// </summary>
public sealed class BrokerResolver
{
    private readonly IReadOnlyDictionary<string, IBrokerAdapterFactory> _factories;

    public BrokerResolver(IEnumerable<IBrokerAdapterFactory> factories)
    {
        _factories = factories.ToDictionary(
            f => f.Type,
            StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<string> AvailableTypes => _factories.Keys.ToArray();

    public IBrokerAdapter Resolve(BrokerConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (!_factories.TryGetValue(config.Type, out var factory))
        {
            throw new NotSupportedException(
                $"Unknown broker type '{config.Type}'. Registered: {string.Join(", ", AvailableTypes)}");
        }

        return factory.Create(config);
    }
}
