using Strategies.Adaptive;
using TradingCore.Abstractions;
using TradingCore.Models;

namespace TradeCop001.Tests;

public class AdaptiveStrategyTests
{
    private static readonly Symbol Symbol = new("BTCUSDT");

    private static AdaptiveOptions FastOptions() => new()
    {
        Symbol = "BTCUSDT",
        FastPeriod = 2, SlowPeriod = 5, RsiPeriod = 3, AtrPeriod = 3, BreakoutPeriod = 4,
        BaseQuantity = 0.01m, StopLossPercent = 1m, TakeProfitPercent = 2m, TrailingStopPercent = 0.5m,
        MinSignalStrength = 0.1m, CooldownBars = 1,
        Mode = TradingMode.Trend, Risk = RiskProfile.Conservative
    };

    private static Bar MakeBar(decimal close, int i, decimal wick = 0.5m) => new(
        Symbol, DateTimeOffset.UtcNow.AddMinutes(i), close, close + wick, close - wick, close, 100m);

    private static List<Bar> Trend(int count, decimal start, decimal step)
    {
        var bars = new List<Bar>();
        var price = start;
        for (var i = 0; i < count; i++)
        {
            price += step;
            bars.Add(MakeBar(price, i));
        }
        return bars;
    }

    private sealed class TestContext : IStrategyContext
    {
        public IBrokerAdapter Broker { get; } = null!;
        public List<OrderRequest> Orders { get; } = new();
        public List<string> Logs { get; } = new();
        public Func<OrderRequest, decimal, OrderResult>? Fill { get; set; }
        public bool ThrowOnOrder { get; set; }
        public void Log(string message) => Logs.Add(message);

        public Task<OrderResult> MarketOrderAsync(Symbol symbol, OrderSide side, decimal quantity,
            string? tag = null, CancellationToken ct = default)
        {
            if (ThrowOnOrder)
                throw new InvalidOperationException("policy rejected order");
            var request = new OrderRequest(symbol, side, OrderType.Market, quantity, ClientOrderId: Guid.NewGuid().ToString("N"), Tag: tag);
            Orders.Add(request);
            return Task.FromResult(Fill?.Invoke(request, 0m)
                ?? new OrderResult("FILL-1", request.ClientOrderId, OrderStatus.Filled, quantity, 1m));
        }
    }

    private sealed class StubAiProvider : IAiSignalProvider
    {
        public string Name => "Stub AI";
        public int Calls { get; private set; }
        public Signal Result { get; set; } = new("AI", SignalDirection.Buy, 1m, "test");

        public Task<Signal?> EvaluateAsync(MarketSnapshot snapshot, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<Signal?>(Result);
        }
    }

    [Fact]
    public void Presets_AreValidAndRiskScalesSize()
    {
        foreach (TradingMode mode in Enum.GetValues<TradingMode>())
        {
            var conservative = AdaptivePresets.For(mode, RiskProfile.Conservative);
            var balanced = AdaptivePresets.For(mode, RiskProfile.Balanced);
            var aggressive = AdaptivePresets.For(mode, RiskProfile.Aggressive);

            AdaptivePresets.Validate(conservative);
            Assert.True(conservative.BaseQuantity < balanced.BaseQuantity);
            Assert.True(balanced.BaseQuantity < aggressive.BaseQuantity);
        }
    }

    [Fact]
    public void Presets_KeepSymbolWhenSwitchingMode()
    {
        var current = AdaptivePresets.For(TradingMode.Trend, RiskProfile.Balanced, "EURUSD", 0.02m);

        var next = AdaptivePresets.WithMode(current, TradingMode.Scalp);

        Assert.Equal("EURUSD", next.Symbol);
        Assert.Equal(TradingMode.Scalp, next.Mode);
        Assert.Equal(3, next.FastPeriod);
    }

    [Fact]
    public void RegimeDetection_DistinguishesTrendAndRange()
    {
        var options = FastOptions();
        var up = Trend(30, 100m, 1m);
        var down = Trend(30, 200m, -1m);
        var range = Enumerable.Range(0, 30)
            .Select(i => MakeBar(100m + (i % 2 == 0 ? 0.2m : -0.2m), i, 0.3m))
            .ToList();

        Assert.Equal(MarketRegime.TrendingUp, MarketRegimeDetector.Detect(up, options));
        Assert.Equal(MarketRegime.TrendingDown, MarketRegimeDetector.Detect(down, options));
        Assert.Equal(MarketRegime.Ranging, MarketRegimeDetector.Detect(range, options));
    }

    [Fact]
    public void Aggregator_ProducesBuyInSustainedUptrend()
    {
        var options = FastOptions();
        var bars = Trend(30, 100m, 1m);
        var signal = new SignalAggregator().Aggregate(bars, options);

        Assert.Equal(SignalDirection.Buy, signal.Direction);
        Assert.True(signal.Strength > 0m);
        Assert.Equal(MarketRegime.TrendingUp, signal.Regime);
        Assert.Equal(3, signal.Signals.Count);
    }

    [Fact]
    public void PositionManager_ExitsOnStopLoss()
    {
        var options = FastOptions();
        var manager = new PositionManager();
        manager.ApplyOpen(SignalDirection.Buy, 0.01m, 100m, 0);
        var flat = AggregatedSignal.Flat(MarketRegime.Ranging, Array.Empty<Signal>());

        var action = manager.Decide(MakeBar(98.5m, 1), options, flat);

        Assert.Equal(TradeActionKind.Close, action.Kind);
        Assert.Contains("stop loss", action.Reason);
    }

    [Fact]
    public void PositionManager_ExitsOnTakeProfit()
    {
        var options = FastOptions();
        var manager = new PositionManager();
        manager.ApplyOpen(SignalDirection.Buy, 0.01m, 100m, 0);

        var action = manager.Decide(MakeBar(103m, 1), options, AggregatedSignal.Flat(MarketRegime.Ranging, Array.Empty<Signal>()));

        Assert.Equal(TradeActionKind.Close, action.Kind);
        Assert.Contains("take profit", action.Reason);
    }

    [Fact]
    public void PositionManager_TrailingStopProtectsProfit()
    {
        var options = FastOptions();
        var manager = new PositionManager();
        manager.ApplyOpen(SignalDirection.Buy, 0.01m, 100m, 0);
        var flat = AggregatedSignal.Flat(MarketRegime.Ranging, Array.Empty<Signal>());

        manager.Decide(MakeBar(102m, 1), options, flat); // new peak, +2%
        var action = manager.Decide(MakeBar(101.4m, 2), options, flat); // retrace 0.59% >= 0.5%

        Assert.Equal(TradeActionKind.Close, action.Kind);
        Assert.Contains("trailing", action.Reason);
    }

    [Fact]
    public void PositionManager_ClosesOnOppositeSignalAndAppliesCooldown()
    {
        var options = FastOptions();
        var manager = new PositionManager();
        manager.ApplyOpen(SignalDirection.Buy, 0.01m, 100m, 0);
        var opposite = new AggregatedSignal(SignalDirection.Sell, 0.7m, MarketRegime.Ranging, Array.Empty<Signal>());

        Assert.Equal(TradeActionKind.Close, manager.Decide(MakeBar(100m, 1), options, opposite).Kind);

        manager.ApplyClose(options.CooldownBars);
        Assert.Equal(1, manager.CooldownRemaining);
        var hold = manager.Decide(MakeBar(100m, 2), options, new AggregatedSignal(SignalDirection.Buy, 1m, MarketRegime.TrendingUp, Array.Empty<Signal>()));
        Assert.Equal(TradeActionKind.Hold, hold.Kind);
        Assert.Contains("cooldown", hold.Reason);
    }

    [Fact]
    public void PositionManager_DoesNotOpenShortWhenDisabled()
    {
        var options = FastOptions();
        options.EnableShorting = false;
        var manager = new PositionManager();
        var sell = new AggregatedSignal(SignalDirection.Sell, 1m, MarketRegime.TrendingDown, Array.Empty<Signal>());

        Assert.Equal(TradeActionKind.Hold, manager.Decide(MakeBar(100m, 0), options, sell).Kind);
    }

    [Fact]
    public async Task Strategy_OpensInUptrendAndClosesOnStopLoss()
    {
        var options = FastOptions();
        options.TakeProfitPercent = 50m;  // avoid profit exits
        options.TrailingStopPercent = 50m; // and avoid trailing exits, isolating the stop-loss path
        options.CooldownBars = 3;
        var strategy = new AdaptiveStrategy(options);
        var context = new TestContext();
        strategy.OnStarted(context);
        strategy.OnWarmupComplete(context);

        foreach (var bar in Trend(25, 170m, 1m))
        {
            var close = bar.Close;
            context.Fill = (_, _) => new OrderResult("FILL", null, OrderStatus.Filled, options.BaseQuantity, close);
            await strategy.OnBarAsync(bar, context);
        }

        Assert.NotEmpty(context.Orders);
        Assert.Equal(OrderSide.Buy, context.Orders[0].Side);
        Assert.NotNull(strategy.Positions.Position);

        context.Fill = (_, _) => new OrderResult("FILL", null, OrderStatus.Filled, options.BaseQuantity, 170m);
        await strategy.OnBarAsync(MakeBar(170m, 100, 5m), context);

        Assert.Equal(OrderSide.Sell, context.Orders[^1].Side);
        Assert.Null(strategy.Positions.Position);
        Assert.Contains(context.Logs, log => log.Contains("stop loss"));
    }

    [Fact]
    public async Task Strategy_BlockedOrderLeavesPositionFlatAndLogsReason()
    {
        var strategy = new AdaptiveStrategy(FastOptions());
        var context = new TestContext { ThrowOnOrder = true };
        strategy.OnStarted(context);
        strategy.OnWarmupComplete(context);

        foreach (var bar in Trend(25, 100m, 1m))
            await strategy.OnBarAsync(bar, context);

        Assert.Null(strategy.Positions.Position);
        Assert.Contains(context.Logs, log => log.Contains("Order blocked"));
    }

    [Fact]
    public async Task Strategy_PresetsAndAiProviderAreControllable()
    {
        var ai = new StubAiProvider();
        var options = FastOptions();
        var strategy = new AdaptiveStrategy(options, ai);
        var context = new TestContext();

        strategy.SetMode(TradingMode.Scalp);
        strategy.SetRisk(RiskProfile.Aggressive);
        Assert.Equal(TradingMode.Scalp, strategy.GetOptions().Mode);
        Assert.Equal(RiskProfile.Aggressive, strategy.GetOptions().Risk);
        var conservativeQty = AdaptivePresets.For(TradingMode.Scalp, RiskProfile.Conservative,
            "BTCUSDT", 0.01m).BaseQuantity;
        Assert.True(strategy.GetOptions().BaseQuantity > conservativeQty);

        var enabled = strategy.GetOptions();
        enabled.AiEnabled = true;
        strategy.UpdateOptions(enabled);

        strategy.OnStarted(context);
        strategy.OnWarmupComplete(context);
        foreach (var bar in Trend(25, 100m, 1m))
            await strategy.OnBarAsync(bar, context);

        Assert.True(ai.Calls > 0);
        Assert.Contains("Stub AI", strategy.GetStatus().AiProvider);
    }

    [Fact]
    public void Strategy_RejectsInvalidOptions()
    {
        var invalid = FastOptions();
        invalid.FastPeriod = 10;
        invalid.SlowPeriod = 5;

        Assert.Throws<ArgumentException>(() => new AdaptiveStrategy(invalid));
    }
}
