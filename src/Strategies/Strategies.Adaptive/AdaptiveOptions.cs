using TradingCore.Models;

namespace Strategies.Adaptive;

/// <summary>Behaviour presets the operator can switch while the app is running.</summary>
public enum TradingMode
{
    Balanced,
    Trend,
    Range,
    Scalp,
    Defensive
}

/// <summary>How much size and stop distance to use. Conservative is the default.</summary>
public enum RiskProfile
{
    Conservative,
    Balanced,
    Aggressive
}

public sealed class AdaptiveOptions
{
    public string Symbol { get; set; } = "BTCUSDT";
    public BarInterval Interval { get; set; } = BarInterval.Minute1;

    public TradingMode Mode { get; set; } = TradingMode.Balanced;
    public RiskProfile Risk { get; set; } = RiskProfile.Conservative;

    public int FastPeriod { get; set; } = 10;
    public int SlowPeriod { get; set; } = 30;
    public int RsiPeriod { get; set; } = 14;
    public int AtrPeriod { get; set; } = 14;
    public int BreakoutPeriod { get; set; } = 20;

    public decimal BaseQuantity { get; set; } = 0.01m;
    public decimal StopLossPercent { get; set; } = 1.5m;
    public decimal TakeProfitPercent { get; set; } = 3m;
    public decimal TrailingStopPercent { get; set; } = 1m;
    public bool EnableShorting { get; set; }

    /// <summary>Bars to sit out after an exit, to avoid immediate re-entry churn.</summary>
    public int CooldownBars { get; set; } = 3;

    /// <summary>Minimum blended signal strength (0..1) required to open a trade.</summary>
    public decimal MinSignalStrength { get; set; } = 0.35m;

    public decimal VolatilityThresholdPercent { get; set; } = 2m;
    public decimal TrendSlopeThresholdPercent { get; set; } = 0.8m;

    /// <summary>Off by default. Only used when an IAiSignalProvider is supplied.</summary>
    public bool AiEnabled { get; set; }

    public AdaptiveOptions Clone() => (AdaptiveOptions)MemberwiseClone();
}

/// <summary>
/// One-tap tuning. A preset replaces all tuning numbers but keeps the symbol and
/// base quantity the operator chose, so switching mode never changes the venue.
/// </summary>
public static class AdaptivePresets
{
    public static AdaptiveOptions For(TradingMode mode, RiskProfile risk, string? symbol = null, decimal? baseQuantity = null)
    {
        var options = mode switch
        {
            TradingMode.Trend => new AdaptiveOptions
            {
                Mode = TradingMode.Trend,
                FastPeriod = 10, SlowPeriod = 30, RsiPeriod = 14, AtrPeriod = 14, BreakoutPeriod = 20,
                StopLossPercent = 2m, TakeProfitPercent = 5m, TrailingStopPercent = 2m,
                MinSignalStrength = 0.40m, CooldownBars = 3
            },
            TradingMode.Range => new AdaptiveOptions
            {
                Mode = TradingMode.Range,
                FastPeriod = 5, SlowPeriod = 20, RsiPeriod = 14, AtrPeriod = 14, BreakoutPeriod = 10,
                StopLossPercent = 1m, TakeProfitPercent = 2m, TrailingStopPercent = 0.75m,
                MinSignalStrength = 0.45m, CooldownBars = 2
            },
            TradingMode.Scalp => new AdaptiveOptions
            {
                Mode = TradingMode.Scalp,
                FastPeriod = 3, SlowPeriod = 10, RsiPeriod = 7, AtrPeriod = 7, BreakoutPeriod = 5,
                StopLossPercent = 0.4m, TakeProfitPercent = 0.8m, TrailingStopPercent = 0.3m,
                MinSignalStrength = 0.50m, CooldownBars = 1
            },
            TradingMode.Defensive => new AdaptiveOptions
            {
                Mode = TradingMode.Defensive,
                FastPeriod = 20, SlowPeriod = 50, RsiPeriod = 21, AtrPeriod = 21, BreakoutPeriod = 30,
                StopLossPercent = 1m, TakeProfitPercent = 2m, TrailingStopPercent = 0.75m,
                MinSignalStrength = 0.60m, CooldownBars = 5
            },
            _ => new AdaptiveOptions
            {
                Mode = TradingMode.Balanced,
                FastPeriod = 8, SlowPeriod = 24, RsiPeriod = 14, AtrPeriod = 14, BreakoutPeriod = 15,
                StopLossPercent = 1.5m, TakeProfitPercent = 3m, TrailingStopPercent = 1m,
                MinSignalStrength = 0.35m, CooldownBars = 3
            }
        };

        options.Risk = risk;
        if (symbol is not null) options.Symbol = symbol;
        if (baseQuantity is not null) options.BaseQuantity = baseQuantity.Value;
        ApplyRisk(options, risk);
        return options;
    }

    public static AdaptiveOptions WithMode(AdaptiveOptions current, TradingMode mode)
    {
        var next = For(mode, current.Risk, current.Symbol, current.BaseQuantity);
        next.Interval = current.Interval;
        next.EnableShorting = current.EnableShorting;
        next.AiEnabled = current.AiEnabled;
        return next;
    }

    public static AdaptiveOptions WithRisk(AdaptiveOptions current, RiskProfile risk)
    {
        var next = For(current.Mode, risk, current.Symbol, current.BaseQuantity);
        next.Interval = current.Interval;
        next.EnableShorting = current.EnableShorting;
        next.AiEnabled = current.AiEnabled;
        return next;
    }

    private static void ApplyRisk(AdaptiveOptions options, RiskProfile risk)
    {
        switch (risk)
        {
            case RiskProfile.Conservative:
                options.BaseQuantity *= 0.5m;
                break;
            case RiskProfile.Balanced:
                break;
            case RiskProfile.Aggressive:
                options.BaseQuantity *= 2m;
                options.StopLossPercent *= 1.25m;
                options.TakeProfitPercent *= 1.25m;
                break;
        }
    }

    public static void Validate(AdaptiveOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Symbol))
            throw new ArgumentException("Symbol is required.", nameof(options));
        if (options.FastPeriod < 2 || options.SlowPeriod <= options.FastPeriod)
            throw new ArgumentException("FastPeriod must be >= 2 and smaller than SlowPeriod.", nameof(options));
        if (options.RsiPeriod < 2 || options.AtrPeriod < 2 || options.BreakoutPeriod < 2)
            throw new ArgumentException("Indicator periods must be at least 2.", nameof(options));
        if (options.BaseQuantity <= 0m)
            throw new ArgumentException("BaseQuantity must be positive.", nameof(options));
        if (options.StopLossPercent <= 0m || options.TakeProfitPercent <= 0m || options.TrailingStopPercent <= 0m)
            throw new ArgumentException("Stop/target/trailing percentages must be positive.", nameof(options));
        if (options.MinSignalStrength is < 0m or > 1m)
            throw new ArgumentException("MinSignalStrength must be between 0 and 1.", nameof(options));
        if (options.CooldownBars < 0)
            throw new ArgumentException("CooldownBars cannot be negative.", nameof(options));
    }
}
