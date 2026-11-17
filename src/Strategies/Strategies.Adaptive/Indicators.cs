using TradingCore.Models;

namespace Strategies.Adaptive;

/// <summary>Small, dependency-free indicator helpers used by the adaptive signals.</summary>
public static class Indicators
{
    public static decimal Sma(IReadOnlyList<decimal> values, int period)
    {
        if (values.Count < period || period <= 0)
            return 0m;
        var sum = 0m;
        for (var i = values.Count - period; i < values.Count; i++)
            sum += values[i];
        return sum / period;
    }

    public static decimal Rsi(IReadOnlyList<decimal> closes, int period)
    {
        if (closes.Count <= period || period <= 0)
            return 50m;

        decimal gains = 0m, losses = 0m;
        for (var i = closes.Count - period; i < closes.Count; i++)
        {
            var change = closes[i] - closes[i - 1];
            if (change > 0m) gains += change;
            else if (change < 0m) losses -= change;
        }

        if (losses == 0m) return gains == 0m ? 50m : 100m;
        var rs = gains / losses;
        return 100m - (100m / (1m + rs));
    }

    /// <summary>Average true range as a percentage of the last close.</summary>
    public static decimal AtrPercent(IReadOnlyList<Bar> bars, int period)
    {
        if (bars.Count < period || period <= 0 || bars.Count == 0)
            return 0m;

        var sum = 0m;
        for (var i = bars.Count - period; i < bars.Count; i++)
        {
            var bar = bars[i];
            var previousClose = i > 0 ? bars[i - 1].Close : bar.Open;
            var trueRange = Math.Max(
                bar.High - bar.Low,
                Math.Max(Math.Abs(bar.High - previousClose), Math.Abs(bar.Low - previousClose)));
            sum += trueRange;
        }

        var average = sum / period;
        var lastClose = bars[^1].Close;
        return lastClose <= 0m ? 0m : average / lastClose * 100m;
    }

    public static decimal Clamp01(decimal value) => value < 0m ? 0m : value > 1m ? 1m : value;
}
