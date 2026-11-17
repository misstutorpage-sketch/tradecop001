using TradeCop001.Licensing;
using TradingCore.Abstractions;
using TradingCore.Models;

namespace TradeCop001.Tests;

/// <summary>
/// News control is a loss-minimisation feature, not a prediction. It must be
/// opt-in, must not call a provider when disabled, and must never let a blocked
/// order reach the venue.
/// </summary>
public class NewsBlackoutGuardTests
{
    private sealed class StubNewsProvider : INewsProvider
    {
        public string Name => "Stub calendar";
        public int Calls { get; private set; }
        public ResearchSnapshot Snapshot { get; set; } = new(Array.Empty<EconomicEvent>());

        public Task<ResearchSnapshot> GetSnapshotAsync(CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Snapshot);
        }
    }

    private static EconomicEvent Event(string impact, int minutesFromNow) => new(
        "EVT-1", DateTimeOffset.UtcNow.AddMinutes(minutesFromNow), "USD", impact, "Nonfarm payrolls");

    private static OrderRequest Market() => new(
        new Symbol("BTCUSDT"), OrderSide.Buy, OrderType.Market, 0.01m,
        ClientOrderId: Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Disabled_DoesNotCallProvider()
    {
        var provider = new StubNewsProvider();
        var guard = new NewsBlackoutGuard(provider) { Enabled = false };

        await guard.EnsureTradingAllowedAsync();

        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task HighImpactEventInsideWindow_BlocksOrder()
    {
        var provider = new StubNewsProvider { Snapshot = new ResearchSnapshot(new[] { Event("High", 5) }) };
        var guard = new NewsBlackoutGuard(provider, minutesBefore: 15, minutesAfter: 15) { Enabled = true };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => guard.EnsureTradingAllowedAsync());

        Assert.Contains("News blackout active", ex.Message);
        Assert.Contains("Nonfarm payrolls", ex.Message);
    }

    [Fact]
    public async Task LowImpactEvent_DoesNotBlock()
    {
        var provider = new StubNewsProvider { Snapshot = new ResearchSnapshot(new[] { Event("Medium", 5) }) };
        var guard = new NewsBlackoutGuard(provider) { Enabled = true };

        await guard.EnsureTradingAllowedAsync(); // no exception
    }

    [Fact]
    public async Task DistantEvent_DoesNotBlock()
    {
        var provider = new StubNewsProvider { Snapshot = new ResearchSnapshot(new[] { Event("High", 120) }) };
        var guard = new NewsBlackoutGuard(provider, minutesBefore: 15, minutesAfter: 15) { Enabled = true };

        await guard.EnsureTradingAllowedAsync(); // no exception
    }

    [Fact]
    public async Task PolicyBroker_WithBlackout_BlocksOrderBeforeItReachesVenue()
    {
        var inner = new TestBroker();
        var provider = new StubNewsProvider { Snapshot = new ResearchSnapshot(new[] { Event("High", 0) }) };
        var guard = new NewsBlackoutGuard(provider) { Enabled = true };
        await using var broker = new PolicyEnforcingBroker(inner, TestPolicy.CreateGuard(), guard);

        await Assert.ThrowsAsync<InvalidOperationException>(() => broker.PlaceOrderAsync(Market()));

        Assert.Empty(inner.Orders);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task PolicyBroker_WithBlackoutDisabled_AllowsOrder()
    {
        var inner = new TestBroker();
        var provider = new StubNewsProvider { Snapshot = new ResearchSnapshot(new[] { Event("High", 0) }) };
        var guard = new NewsBlackoutGuard(provider) { Enabled = false };
        await using var broker = new PolicyEnforcingBroker(inner, TestPolicy.CreateGuard(), guard);

        var result = await broker.PlaceOrderAsync(Market());

        Assert.Equal(OrderStatus.Filled, result.Status);
        Assert.Single(inner.Orders);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public void Constructor_RejectsNullProvider()
    {
        Assert.Throws<ArgumentNullException>(() => new NewsBlackoutGuard(null!));
    }
}
