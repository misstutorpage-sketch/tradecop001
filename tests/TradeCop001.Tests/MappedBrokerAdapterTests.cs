using TradingCore.Adapters;
using TradingCore.Models;

namespace TradeCop001.Tests;

/// <summary>
/// The strategy keeps speaking canonical symbols while the venue sees its own
/// names. Both directions are covered here.
/// </summary>
public class MappedBrokerAdapterTests
{
    private static IReadOnlyDictionary<string, string> DerivLikeMap() => new Dictionary<string, string>
    {
        ["BTCUSDT"] = "BTCUSD",
        ["VOL75"] = "Volatility 75 Index"
    };

    private static OrderRequest Market(string symbol, decimal qty) => new(
        new Symbol(symbol), OrderSide.Buy, OrderType.Market, qty,
        ClientOrderId: Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PlaceOrder_MapsSymbolToVenueName()
    {
        var inner = new TestBroker();
        await using var mapped = new MappedBrokerAdapter(inner, DerivLikeMap());

        await mapped.PlaceOrderAsync(Market("BTCUSDT", 0.01m));

        Assert.Single(inner.Orders);
        Assert.Equal("BTCUSD", inner.Orders[0].Symbol.Value);
    }

    [Fact]
    public async Task HistoricalBars_MapBackToCanonicalSymbol()
    {
        var inner = new TestBroker
        {
            History = new[]
            {
                new Bar(new Symbol("Volatility 75 Index"), DateTimeOffset.UtcNow, 1m, 2m, 0.5m, 1.5m, 10m)
            }
        };
        await using var mapped = new MappedBrokerAdapter(inner, DerivLikeMap());

        var bars = await mapped.GetHistoricalBarsAsync(new Symbol("VOL75"), BarInterval.Minute1, 1);

        Assert.Single(bars);
        Assert.Equal("VOL75", bars[0].Symbol.Value);
    }

    [Fact]
    public async Task StreamBars_MapBackToCanonicalSymbol()
    {
        var inner = new TestBroker();
        await using var mapped = new MappedBrokerAdapter(inner, DerivLikeMap());
        inner.LiveBars.Writer.TryWrite(
            new Bar(new Symbol("BTCUSD"), DateTimeOffset.UtcNow, 1m, 2m, 0.5m, 1.5m, 10m));
        inner.LiveBars.Writer.TryComplete();

        var seen = new List<Bar>();
        await foreach (var bar in mapped.StreamBarsAsync(new Symbol("BTCUSDT"), BarInterval.Minute1))
            seen.Add(bar);

        Assert.Single(seen);
        Assert.Equal("BTCUSDT", seen[0].Symbol.Value);
    }

    [Fact]
    public async Task UnmappedSymbol_PassesThroughUnchanged()
    {
        var inner = new TestBroker();
        await using var mapped = new MappedBrokerAdapter(inner, DerivLikeMap());

        Assert.Equal("EURUSD", mapped.ToVenue(new Symbol("EURUSD")).Value);
        Assert.Equal("EURUSD", mapped.ToCanonical(new Symbol("EURUSD")).Value);
    }

    [Fact]
    public async Task Dispose_DelegatesToInnerBroker()
    {
        var inner = new TestBroker();
        var mapped = new MappedBrokerAdapter(inner, DerivLikeMap());

        await mapped.DisposeAsync();

        Assert.Contains("dispose", inner.Events);
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        var inner = new TestBroker();

        Assert.Throws<ArgumentNullException>(() => new MappedBrokerAdapter(null!, DerivLikeMap()));
        Assert.Throws<ArgumentNullException>(() => new MappedBrokerAdapter(inner, null!));
    }
}
