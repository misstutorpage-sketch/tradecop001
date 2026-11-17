using TradingBrokers.Paper;
using TradingCore.Configuration;
using TradingCore.Models;

namespace TradeCop001.Tests;

public class PaperBrokerTests
{
    private static PaperBrokerAdapter MakeBroker() => new(new BrokerConfig
    {
        Type = "paper",
        Settings = new Dictionary<string, string>
        {
            ["Symbols"] = "BTCUSDT",
            ["StartingCash"] = "100000",
            ["Seed"] = "42",
            ["BarIntervalMs"] = "50"
        }
    });

    private static OrderRequest Market(string symbol, OrderSide side, decimal qty) => new(
        new Symbol(symbol), side, OrderType.Market, qty, ClientOrderId: Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Connect_SetsIsConnected()
    {
        await using var broker = MakeBroker();

        Assert.False(broker.IsConnected);
        await broker.ConnectAsync();
        Assert.True(broker.IsConnected);
    }

    [Fact]
    public async Task HistoricalBars_ReturnsRequestedCount()
    {
        await using var broker = MakeBroker();

        var bars = await broker.GetHistoricalBarsAsync(new Symbol("BTCUSDT"), BarInterval.Minute1, 30);

        Assert.Equal(30, bars.Count);
        Assert.All(bars, b => Assert.True(b.High >= b.Low));
    }

    [Fact]
    public async Task MarketBuy_CreatesPositionAndFills()
    {
        await using var broker = MakeBroker();
        await broker.ConnectAsync();

        var result = await broker.PlaceOrderAsync(Market("BTCUSDT", OrderSide.Buy, 0.5m));

        Assert.Equal(OrderStatus.Filled, result.Status);
        Assert.Equal(0.5m, result.FilledQuantity);
        Assert.StartsWith("PAPER-", result.BrokerOrderId);

        var positions = await broker.GetPositionsAsync();
        Assert.Single(positions);
        Assert.Equal(0.5m, positions[0].Quantity);
    }

    [Fact]
    public async Task BuyThenSell_FlattensPosition()
    {
        await using var broker = MakeBroker();
        await broker.ConnectAsync();

        await broker.PlaceOrderAsync(Market("BTCUSDT", OrderSide.Buy, 1m));
        await broker.PlaceOrderAsync(Market("BTCUSDT", OrderSide.Sell, 1m));

        var positions = await broker.GetPositionsAsync();
        Assert.Equal(0m, positions[0].Quantity);
    }

    [Fact]
    public async Task Account_ShowsExpectedStartingCash()
    {
        await using var broker = MakeBroker();
        await broker.ConnectAsync();

        var account = await broker.GetAccountAsync();

        Assert.Equal("PAPER-001", account.AccountId);
        Assert.Equal("USD", account.Currency);
        Assert.True(account.Equity > 0m);
    }

    [Fact]
    public async Task CancelOrder_MarksCancelled()
    {
        await using var broker = MakeBroker();
        await broker.ConnectAsync();
        var result = await broker.PlaceOrderAsync(Market("BTCUSDT", OrderSide.Buy, 0.1m));

        OrderResult? updated = null;
        broker.OrderUpdated += (_, e) => updated = e;
        await broker.CancelOrderAsync(result.BrokerOrderId);

        Assert.NotNull(updated);
        Assert.Equal(OrderStatus.Cancelled, updated.Status);
    }
}
