using TradingCore.Abstractions;
using TradingCore.Configuration;

namespace TradingBrokers.MT5;

public sealed class MT5BrokerFactory : IBrokerAdapterFactory
{
    public string Type => "mt5";
    public IBrokerAdapter Create(BrokerConfig config) => new MetaTrader5Adapter(config);
}
