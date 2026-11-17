using TradingBrokers.Bridge;
using TradingCore.Configuration;
using TradingCore.Models;

namespace TradingBrokers.IB;

/// <summary>
/// Interactive Brokers via a local bridge process using the official IBApi
/// (EClient/EWrapper). The bridge translates TWS/Gateway calls into the JSON-RPC
/// protocol; this adapter stays identical in shape to the MT5 adapter.
/// </summary>
public sealed class InteractiveBrokersAdapter : BridgeBrokerAdapterBase
{
    public override string Name => "Interactive Brokers";

    public InteractiveBrokersAdapter(BrokerConfig config) : base(config) { }

    protected override int DefaultPort => 5051;

    protected override string MapInterval(BarInterval interval) => interval switch
    {
        BarInterval.Minute1 => "1 min",
        BarInterval.Minute5 => "5 mins",
        BarInterval.Minute15 => "15 mins",
        BarInterval.Hour1 => "1 hour",
        BarInterval.Day1 => "1 day",
        _ => "1 min"
    };
}
