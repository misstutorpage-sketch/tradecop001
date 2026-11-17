using TradingBrokers.Bridge;
using TradingCore.Configuration;
using TradingCore.Models;

namespace TradingBrokers.MT5;

/// <summary>
/// MetaTrader 5 via an MQL5 Expert Advisor that listens on a local TCP port.
/// MT5 has no official .NET API, so the EA is the native side of the bridge.
/// See BridgeProtocol.md for the message contract.
/// </summary>
public sealed class MetaTrader5Adapter : BridgeBrokerAdapterBase
{
    public override string Name => "MetaTrader 5";

    public MetaTrader5Adapter(BrokerConfig config) : base(config) { }

    protected override int DefaultPort => 5052;

    protected override string MapInterval(BarInterval interval) => interval switch
    {
        BarInterval.Minute1 => "M1",
        BarInterval.Minute5 => "M5",
        BarInterval.Minute15 => "M15",
        BarInterval.Hour1 => "H1",
        BarInterval.Day1 => "D1",
        _ => "M1"
    };
}
