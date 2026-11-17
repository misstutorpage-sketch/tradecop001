using TradingCore.Abstractions;
using TradingCore.Configuration;

namespace TradingBrokers.Crypto;

public sealed class CryptoBrokerFactory : IBrokerAdapterFactory
{
    public string Type => "crypto";
    public IBrokerAdapter Create(BrokerConfig config) => new CryptoRestBrokerAdapter(config);
}
