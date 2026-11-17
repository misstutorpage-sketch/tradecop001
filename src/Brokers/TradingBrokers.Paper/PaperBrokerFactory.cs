using TradingCore.Abstractions;
using TradingCore.Configuration;

namespace TradingBrokers.Paper;

public sealed class PaperBrokerFactory : IBrokerAdapterFactory
{
    public string Type => "paper";
    public IBrokerAdapter Create(BrokerConfig config) => new PaperBrokerAdapter(config);
}
