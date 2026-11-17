using TradingCore.Abstractions;
using TradingCore.Configuration;

namespace TradingBrokers.IB;

public sealed class IBBrokerFactory : IBrokerAdapterFactory
{
    public string Type => "ib";
    public IBrokerAdapter Create(BrokerConfig config) => new InteractiveBrokersAdapter(config);
}
