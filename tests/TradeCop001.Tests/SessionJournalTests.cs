using TradingCore.Engine;
using TradingCore.Models;

namespace TradeCop001.Tests;

public class SessionJournalTests
{
    private static OrderRequest Order(string symbol, OrderSide side, decimal qty) => new(
        new Symbol(symbol), side, OrderType.Market, qty,
        ClientOrderId: Guid.NewGuid().ToString("N"), Tag: "test");

    [Fact]
    public async Task JournalingBroker_RecordsFilledOrder()
    {
        var journal = new SessionJournal();
        await using var broker = new JournalingBrokerAdapter(new TestBroker(), journal);

        await broker.PlaceOrderAsync(Order("BTCUSDT", OrderSide.Buy, 0.01m));

        var entry = Assert.Single(journal.All());
        Assert.Equal("BTCUSDT", entry.Symbol);
        Assert.Equal(OrderSide.Buy, entry.Side);
        Assert.Equal(OrderStatus.Filled, entry.Status);
        Assert.Equal(0.01m, entry.FilledQuantity);
        Assert.True(entry.IsFill);
        Assert.Equal("test", entry.Tag);
    }

    [Fact]
    public async Task JournalingBroker_RecordsRejectedOrderWithoutMarkingItAsFill()
    {
        var inner = new TestBroker
        {
            OnOrder = request => new OrderResult("", request.ClientOrderId, OrderStatus.Rejected, 0m, 0m, "no")
        };
        var journal = new SessionJournal();
        await using var broker = new JournalingBrokerAdapter(inner, journal);

        await broker.PlaceOrderAsync(Order("EURUSD", OrderSide.Sell, 0.02m));

        var entry = Assert.Single(journal.All());
        Assert.Equal(OrderStatus.Rejected, entry.Status);
        Assert.False(entry.IsFill);
    }

    [Fact]
    public async Task JournalingBroker_DelegatesAccountAndPositions()
    {
        var journal = new SessionJournal();
        await using var broker = new JournalingBrokerAdapter(new TestBroker { Equity = 1234m }, journal);

        var account = await broker.GetAccountAsync();
        var positions = await broker.GetPositionsAsync();

        Assert.Equal(1234m, account.Equity);
        Assert.Empty(positions);
        Assert.Empty(journal.All());
    }

    [Fact]
    public void Since_FiltersOldEntries()
    {
        var journal = new SessionJournal();
        var request = Order("XAUUSD", OrderSide.Buy, 0.01m);
        var result = new OrderResult("1", request.ClientOrderId, OrderStatus.Filled, 0.01m, 2400m);

        journal.Record(request, result);

        Assert.Single(journal.Since(DateTimeOffset.Now.AddMinutes(-1)));
        Assert.Empty(journal.Since(DateTimeOffset.Now.AddMinutes(1)));
    }

    [Fact]
    public void Capacity_DropsOldestEntries()
    {
        var journal = new SessionJournal(capacity: 2);
        var request = Order("BTCUSDT", OrderSide.Buy, 0.01m);

        for (var i = 0; i < 3; i++)
        {
            journal.Record(request, new OrderResult(
                $"id-{i}", request.ClientOrderId, OrderStatus.Filled, 0.01m, 100m + i));
        }

        var entries = journal.All();
        Assert.Equal(2, entries.Count);
        Assert.Equal("id-1", entries[0].BrokerOrderId);
        Assert.Equal("id-2", entries[1].BrokerOrderId);
    }
}
