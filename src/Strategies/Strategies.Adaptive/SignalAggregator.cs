using TradingCore.Models;

namespace Strategies.Adaptive;

public enum MarketRegime
{
    TrendingUp,
    TrendingDown,
    Ranging,
    Volatile
}

public sealed record AggregatedSignal(
    SignalDirection Direction,
    decimal Strength,
    MarketRegime Regime,
    IReadOnlyList<Signal> Signals)
{
    public static AggregatedSignal Flat(MarketRegime regime, IReadOnlyList<Signal> signals)
        => new(SignalDirection.Flat, 0m, regime, signals);
}

/// <summary>
/// Classifies the current market so signal weights can adapt. 'Morphic' here means
/// the same code re-weights its inputs as conditions change — no hidden state, no
/// network calls, and fully reproducible in tests.
/// </summary>
public static class MarketRegimeDetector
{
    public static MarketRegime Detect(IReadOnlyList<Bar> bars, AdaptiveOptions options)
    {
        if (bars.Count < Math.Max(options.SlowPeriod, options.AtrPeriod))
            return MarketRegime.Ranging;

        var volatility = Indicators.AtrPercent(bars, options.AtrPeriod);
        if (volatility >= options.VolatilityThresholdPercent)
            return MarketRegime.Volatile;

        var closes = bars.Select(b => b.Close).ToArray();
        var slow = Indicators.Sma(closes, options.SlowPeriod);
        if (slow <= 0m)
            return MarketRegime.Ranging;

        var slope = (closes[^1] - slow) / slow * 100m;
        if (slope >= options.TrendSlopeThresholdPercent)
            return MarketRegime.TrendingUp;
        if (slope <= -options.TrendSlopeThresholdPercent)
            return MarketRegime.TrendingDown;

        return MarketRegime.Ranging;
    }
}

/// <summary>Blends signals using regime-specific weights into one actionable signal.</summary>
public sealed class SignalAggregator
{
    private readonly IReadOnlyList<IMarketSignal> _signals;

    public SignalAggregator(IReadOnlyList<IMarketSignal>? signals = null)
    {
        _signals = signals ?? new IMarketSignal[]
        {
            new TrendSignal(),
            new MomentumSignal(),
            new BreakoutSignal()
        };
    }

    public IReadOnlyList<string> SignalNames => _signals.Select(s => s.Name).ToArray();

    public AggregatedSignal Aggregate(IReadOnlyList<Bar> bars, AdaptiveOptions options)
    {
        var regime = MarketRegimeDetector.Detect(bars, options);
        var signals = _signals.Select(s => s.Evaluate(bars, options)).ToArray();

        decimal buyScore = 0m, sellScore = 0m, totalWeight = 0m;
        foreach (var signal in signals)
        {
            var weight = Weight(regime, signal.Strategy);
            totalWeight += weight;
            if (signal.Direction == SignalDirection.Buy)
                buyScore += weight * signal.Strength;
            else if (signal.Direction == SignalDirection.Sell)
                sellScore += weight * signal.Strength;
        }

        if (totalWeight <= 0m)
            return AggregatedSignal.Flat(regime, signals);

        var net = (buyScore - sellScore) / totalWeight;
        var direction = net > 0m ? SignalDirection.Buy : net < 0m ? SignalDirection.Sell : SignalDirection.Flat;
        var strength = Math.Abs(net);
        return new AggregatedSignal(direction, strength, regime, signals);
    }

    private static decimal Weight(MarketRegime regime, string signal) => (regime, signal) switch
    {
        (MarketRegime.TrendingUp or MarketRegime.TrendingDown, "Trend") => 1.0m,
        (MarketRegime.TrendingUp or MarketRegime.TrendingDown, "Breakout") => 0.8m,
        (MarketRegime.TrendingUp or MarketRegime.TrendingDown, "Momentum") => 0.2m,
        (MarketRegime.Ranging, "Momentum") => 1.0m,
        (MarketRegime.Ranging, "Trend") => 0.2m,
        (MarketRegime.Ranging, "Breakout") => 0.3m,
        (MarketRegime.Volatile, "Momentum") => 0.6m,
        (MarketRegime.Volatile, "Breakout") => 0.5m,
        (MarketRegime.Volatile, "Trend") => 0.3m,
        _ => 0.5m
    };
}
