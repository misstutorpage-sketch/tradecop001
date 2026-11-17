using Strategies.MovingAverage;
using TradingCore.Abstractions;
using TradingCore.Models;

namespace TradeCop001.Tests;

public class SmaCrossoverStrategyTests
{
    private sealed class RecordingContext : IStrategyContext
    {
        public IBrokerAdapter Broker { get; } = null!;
        public List<(OrderSide Side, decimal Qty)> Orders { get; } = new();
        public List<string> Logs { get; } = new();
        public CancellationToken ObservedCancellationToken { get; private set; }
        public void Log(string message) => Logs.Add(message);

        public Task<OrderResult> MarketOrderAsync(Symbol symbol, OrderSide side, decimal quantity,
            string? tag = null, CancellationToken ct = default)
        {
            Orders.Add((side, quantity));
            ObservedCancellationToken = ct;
            return Task.FromResult(new OrderResult("fake", null, OrderStatus.Filled, quantity, 1m));
        }
    }

    private static Bar BarAt(decimal close, int i) => new(
        new Symbol("BTCUSDT"), DateTimeOffset.UtcNow.AddMinutes(i), close, close, close, close, 100m);

    private static (SmaCrossoverStrategy Strategy, RecordingContext Context) MakeStrategy()
    {
        var strategy = new SmaCrossoverStrategy(new SmaCrossoverOptions
        {
            Symbol = "BTCUSDT", FastPeriod = 2, SlowPeriod = 3, Quantity = 0.01m
        });
        var ctx = new RecordingContext();
        strategy.OnStarted(ctx);
        return (strategy, ctx);
    }

    private static async Task FeedDowntrendAsync(SmaCrossoverStrategy strategy, RecordingContext ctx)
    {
        var down = new[] { 30m, 20m, 10m, 10m };
        for (var i = 0; i < down.Length; i++)
            await strategy.OnBarAsync(BarAt(down[i], i), ctx);
    }

    [Fact]
    public async Task NoOrdersBeforeWarmupComplete()
    {
        var (strategy, ctx) = MakeStrategy();

        // climbing prices would normally trigger a golden cross
        var closes = new[] { 10m, 11m, 12m, 13m, 14m, 15m, 16m };
        for (var i = 0; i < closes.Length; i++)
            await strategy.OnBarAsync(BarAt(closes[i], i), ctx);

        Assert.Empty(ctx.Orders);
    }

    [Fact]
    public async Task GoldenCross_PlacesBuyAfterWarmup()
    {
        var (strategy, ctx) = MakeStrategy();

        await FeedDowntrendAsync(strategy, ctx);
        strategy.OnWarmupComplete(ctx);

        // then reverse upward so fast SMA crosses above slow SMA
        await strategy.OnBarAsync(BarAt(30m, 10), ctx);

        Assert.Single(ctx.Orders);
        Assert.Equal(OrderSide.Buy, ctx.Orders[0].Side);
        Assert.Equal(0.01m, ctx.Orders[0].Qty);
    }

    [Fact]
    public async Task DeathCross_PlacesSellOnlyWhenLong()
    {
        var (strategy, ctx) = MakeStrategy();

        await FeedDowntrendAsync(strategy, ctx);
        strategy.OnWarmupComplete(ctx);

        // death cross while flat -> no order
        await strategy.OnBarAsync(BarAt(5m, 10), ctx);
        Assert.Empty(ctx.Orders);

        // establish an uptrend to go long
        await strategy.OnBarAsync(BarAt(30m, 11), ctx);
        await strategy.OnBarAsync(BarAt(40m, 12), ctx);
        Assert.NotEmpty(ctx.Orders);
        Assert.Equal(OrderSide.Buy, ctx.Orders[^1].Side);

        // reverse downward -> sell
        await strategy.OnBarAsync(BarAt(10m, 13), ctx);
        Assert.Equal(OrderSide.Sell, ctx.Orders[^1].Side);
    }

    [Fact]
    public async Task OnBarAsync_PropagatesCancellationTokenToOrder()
    {
        var (strategy, ctx) = MakeStrategy();
        await FeedDowntrendAsync(strategy, ctx);
        strategy.OnWarmupComplete(ctx);
        using var cts = new CancellationTokenSource();

        await strategy.OnBarAsync(BarAt(30m, 10), ctx, cts.Token);

        Assert.Single(ctx.Orders);
        Assert.Equal(cts.Token, ctx.ObservedCancellationToken);
    }

    [Fact]
    public void RejectsInvalidPeriods()
        => Assert.Throws<ArgumentException>(() => new SmaCrossoverStrategy(
            new SmaCrossoverOptions { FastPeriod = 30, SlowPeriod = 10 }));
}
