using TradingCore.Models;

namespace Strategies.Adaptive;

public enum TradeActionKind
{
    Hold,
    OpenLong,
    OpenShort,
    Close
}

public sealed record TradeAction(TradeActionKind Kind, string Reason)
{
    public static TradeAction Hold(string reason) => new(TradeActionKind.Hold, reason);
    public static TradeAction Close(string reason) => new(TradeActionKind.Close, reason);
}

/// <summary>Our own view of the open trade. The broker remains the source of truth for fills.</summary>
public sealed record OpenPosition(
    SignalDirection Direction,
    decimal Quantity,
    decimal EntryPrice,
    int EntryBarIndex);

/// <summary>
/// Pure decision logic for entries and exits: stop loss, take profit, trailing stop,
/// opposite-signal exit and cooldown. State changes only after a confirmed fill.
/// </summary>
public sealed class PositionManager
{
    public OpenPosition? Position { get; private set; }
    public int CooldownRemaining { get; private set; }
    public decimal? UnrealizedPnLPercent { get; private set; }

    private decimal _extreme;

    public void Reset()
    {
        Position = null;
        CooldownRemaining = 0;
        UnrealizedPnLPercent = null;
        _extreme = 0m;
    }

    public TradeAction Decide(Bar bar, AdaptiveOptions options, AggregatedSignal signal)
    {
        if (CooldownRemaining > 0)
        {
            CooldownRemaining--;
            if (Position is null)
                return TradeAction.Hold("cooldown after last exit");
        }

        if (Position is not null)
        {
            var pnl = PnLPercent(Position, bar.Close);
            UnrealizedPnLPercent = pnl;

            _extreme = Position.Direction == SignalDirection.Buy
                ? Math.Max(_extreme, bar.Close)
                : Math.Min(_extreme, bar.Close);

            if (pnl <= -options.StopLossPercent)
                return TradeAction.Close($"stop loss ({pnl:F2}%)");
            if (pnl >= options.TakeProfitPercent)
                return TradeAction.Close($"take profit ({pnl:F2}%)");

            if (pnl > 0m && _extreme > 0m)
            {
                var retrace = Position.Direction == SignalDirection.Buy
                    ? (_extreme - bar.Close) / _extreme * 100m
                    : (bar.Close - _extreme) / _extreme * 100m;
                if (retrace >= options.TrailingStopPercent)
                    return TradeAction.Close($"trailing stop ({retrace:F2}% from extreme)");
            }

            var opposite = Position.Direction == SignalDirection.Buy
                ? signal.Direction == SignalDirection.Sell
                : signal.Direction == SignalDirection.Buy;
            if (opposite && signal.Strength >= options.MinSignalStrength)
                return TradeAction.Close("opposite signal confirmed");

            return TradeAction.Hold("position open");
        }

        UnrealizedPnLPercent = null;

        if (signal.Direction == SignalDirection.Flat || signal.Strength < options.MinSignalStrength)
            return TradeAction.Hold("no qualifying signal");

        if (signal.Direction == SignalDirection.Sell && !options.EnableShorting)
            return TradeAction.Hold("shorting disabled by options");

        return signal.Direction == SignalDirection.Buy
            ? new TradeAction(TradeActionKind.OpenLong, "blended buy signal")
            : new TradeAction(TradeActionKind.OpenShort, "blended sell signal");
    }

    public void ApplyOpen(SignalDirection direction, decimal quantity, decimal fillPrice, int barIndex)
    {
        Position = new OpenPosition(direction, quantity, fillPrice, barIndex);
        _extreme = fillPrice;
    }

    public void ApplyClose(int cooldownBars)
    {
        Position = null;
        UnrealizedPnLPercent = null;
        CooldownRemaining = Math.Max(0, cooldownBars);
        _extreme = 0m;
    }

    private static decimal PnLPercent(OpenPosition position, decimal price)
    {
        if (position.EntryPrice <= 0m)
            return 0m;
        return position.Direction == SignalDirection.Buy
            ? (price - position.EntryPrice) / position.EntryPrice * 100m
            : (position.EntryPrice - price) / position.EntryPrice * 100m;
    }
}
