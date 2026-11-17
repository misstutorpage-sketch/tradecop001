using TradingCore.Models;

namespace Strategies.Adaptive;

public enum SignalDirection
{
    Buy,
    Sell,
    Flat
}

public sealed record Signal(string Strategy, SignalDirection Direction, decimal Strength, string Reason)
{
    public static Signal Flat(string strategy, string reason) => new(strategy, SignalDirection.Flat, 0m, reason);
}

/// <summary>One independent view of the market. Signals never place orders.</summary>
public interface IMarketSignal
{
    string Name { get; }
    Signal Evaluate(IReadOnlyList<Bar> bars, AdaptiveOptions options);
}

/// <summary>Fast/slow SMA relationship — the trend-following view.</summary>
public sealed class TrendSignal : IMarketSignal
{
    public string Name => "Trend";

    public Signal Evaluate(IReadOnlyList<Bar> bars, AdaptiveOptions options)
    {
        if (bars.Count < options.SlowPeriod)
            return Signal.Flat(Name, "not enough bars for trend");

        var closes = bars.Select(b => b.Close).ToArray();
        var fast = Indicators.Sma(closes, options.FastPeriod);
        var slow = Indicators.Sma(closes, options.SlowPeriod);
        var atr = Indicators.AtrPercent(bars, options.AtrPeriod);
        var spread = fast - slow;
        var strength = Indicators.Clamp01(Math.Abs(spread) / Math.Max(atr, 0.01m) * 0.5m);

        return spread > 0m
            ? new Signal(Name, SignalDirection.Buy, strength, "fast SMA above slow SMA")
            : spread < 0m
                ? new Signal(Name, SignalDirection.Sell, strength, "fast SMA below slow SMA")
                : Signal.Flat(Name, "SMA spread is flat");
    }
}

/// <summary>RSI mean-reversion — the 'stretched price' view.</summary>
public sealed class MomentumSignal : IMarketSignal
{
    public string Name => "Momentum";

    public Signal Evaluate(IReadOnlyList<Bar> bars, AdaptiveOptions options)
    {
        if (bars.Count <= options.RsiPeriod)
            return Signal.Flat(Name, "not enough bars for RSI");

        var closes = bars.Select(b => b.Close).ToArray();
        var rsi = Indicators.Rsi(closes, options.RsiPeriod);
        const decimal oversold = 30m, overbought = 70m;

        if (rsi < oversold)
            return new Signal(Name, SignalDirection.Buy,
                Indicators.Clamp01((oversold - rsi) / oversold), $"RSI oversold ({rsi:F1})");
        if (rsi > overbought)
            return new Signal(Name, SignalDirection.Sell,
                Indicators.Clamp01((rsi - overbought) / (100m - overbought)), $"RSI overbought ({rsi:F1})");

        return Signal.Flat(Name, $"RSI neutral ({rsi:F1})");
    }
}

/// <summary>Price breaking a recent range — the breakout view.</summary>
public sealed class BreakoutSignal : IMarketSignal
{
    public string Name => "Breakout";

    public Signal Evaluate(IReadOnlyList<Bar> bars, AdaptiveOptions options)
    {
        var period = options.BreakoutPeriod;
        if (bars.Count <= period)
            return Signal.Flat(Name, "not enough bars for breakout");

        var window = bars.TakeLast(period + 1).Take(period).ToArray(); // exclude the current bar
        var highest = window.Max(b => b.High);
        var lowest = window.Min(b => b.Low);
        var close = bars[^1].Close;
        var range = Math.Max(highest - lowest, 0.00000001m);

        if (close > highest)
            return new Signal(Name, SignalDirection.Buy,
                Indicators.Clamp01((close - highest) / range), "close broke recent high");
        if (close < lowest)
            return new Signal(Name, SignalDirection.Sell,
                Indicators.Clamp01((lowest - close) / range), "close broke recent low");

        return Signal.Flat(Name, "inside recent range");
    }
}
