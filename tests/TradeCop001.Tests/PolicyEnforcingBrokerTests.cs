using TradeCop001.Licensing;
using TradingCore.Models;

namespace TradeCop001.Tests;

/// <summary>
/// The decorator is the last line of defence before an order reaches a venue,
/// so these tests verify that rejected orders never touch the underlying broker.
/// </summary>
public class PolicyEnforcingBrokerTests
{
    private static OrderRequest Market(string symbol, decimal qty, OrderSide side = OrderSide.Buy) => new(
        new Symbol(symbol), side, OrderType.Market, qty, ClientOrderId: Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task PlaceOrder_AllowsCompliantOrder()
    {
        var inner = new TestBroker();
        var guard = TestPolicy.CreateGuard();
        await using var broker = new PolicyEnforcingBroker(inner, guard);

        var result = await broker.PlaceOrderAsync(Market("BTCUSDT", 0.01m));

        Assert.Equal(OrderStatus.Filled, result.Status);
        Assert.Single(inner.Orders);
    }

    [Fact]
    public async Task PlaceOrder_RejectsOversizedOrder_WithoutCallingInner()
    {
        var inner = new TestBroker();
        var guard = TestPolicy.CreateGuard();
        await using var broker = new PolicyEnforcingBroker(inner, guard);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.PlaceOrderAsync(Market("BTCUSDT", 1m)));

        Assert.Empty(inner.Orders);
    }

    [Fact]
    public async Task PlaceOrder_RejectsDisallowedSymbol()
    {
        var inner = new TestBroker();
        var guard = TestPolicy.CreateGuard();
        await using var broker = new PolicyEnforcingBroker(inner, guard);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.PlaceOrderAsync(Market("ETHUSDT", 0.01m)));

        Assert.Empty(inner.Orders);
    }

    [Fact]
    public async Task PlaceOrder_RejectsWhenPaused()
    {
        var inner = new TestBroker();
        var guard = TestPolicy.CreateGuard();
        await using var broker = new PolicyEnforcingBroker(inner, guard);

        guard.Pause();
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.PlaceOrderAsync(Market("BTCUSDT", 0.01m)));
        Assert.Empty(inner.Orders);

        guard.Resume();
        await broker.PlaceOrderAsync(Market("BTCUSDT", 0.01m));
        Assert.Single(inner.Orders);
    }

    [Fact]
    public async Task PlaceOrder_RejectsWhenTradingDisabled()
    {
        var inner = new TestBroker();
        var guard = TestPolicy.CreateGuard();
        await using var broker = new PolicyEnforcingBroker(inner, guard);

        guard.Policy.TradingEnabled = false;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.PlaceOrderAsync(Market("BTCUSDT", 0.01m)));
        Assert.Empty(inner.Orders);
    }

    [Fact]
    public async Task PlaceOrder_RejectsOutsideTradingWindow()
    {
        var inner = new TestBroker();
        var policy = TestPolicy.Valid();
        var closedMinute = TimeOnly.FromDateTime(DateTime.UtcNow).AddMinutes(-2);
        policy.StartUtc = closedMinute;
        policy.EndUtc = closedMinute; // window was two minutes ago, so it is closed now
        var guard = TestPolicy.CreateGuard(policy);
        await using var broker = new PolicyEnforcingBroker(inner, guard);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.PlaceOrderAsync(Market("BTCUSDT", 0.01m)));
        Assert.Empty(inner.Orders);
    }

    [Fact]
    public async Task PlaceOrder_EnforcesDailyLossAfterBaselineIsSet()
    {
        var inner = new TestBroker { Equity = 1_000m };
        var guard = TestPolicy.CreateGuard(TestPolicy.Valid(maxDailyLoss: 100m));
        await using var broker = new PolicyEnforcingBroker(inner, guard);

        await broker.PlaceOrderAsync(Market("BTCUSDT", 0.01m)); // establishes 1_000 baseline
        Assert.Single(inner.Orders);

        inner.Equity = 880m; // loss of 120 > 100
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => broker.PlaceOrderAsync(Market("BTCUSDT", 0.01m)));

        Assert.Single(inner.Orders); // second order blocked before reaching inner broker
    }

    [Fact]
    public async Task Name_ReflectsPolicyEnforcement()
    {
        var inner = new TestBroker();
        var guard = TestPolicy.CreateGuard();
        await using var broker = new PolicyEnforcingBroker(inner, guard);

        Assert.Equal("Test Broker [policy enforced]", broker.Name);
    }

    [Fact]
    public async Task OrderUpdated_ForwardsInnerNotification()
    {
        var inner = new TestBroker();
        var guard = TestPolicy.CreateGuard();
        await using var broker = new PolicyEnforcingBroker(inner, guard);
        OrderResult? seen = null;
        broker.OrderUpdated += (_, e) => seen = e;

        await broker.PlaceOrderAsync(Market("BTCUSDT", 0.01m));

        Assert.NotNull(seen);
        Assert.Equal(OrderStatus.Filled, seen.Status);
    }

    [Fact]
    public async Task ConnectAndDispose_DelegateToInnerBroker()
    {
        var inner = new TestBroker();
        var guard = TestPolicy.CreateGuard();
        var broker = new PolicyEnforcingBroker(inner, guard);

        await broker.ConnectAsync();
        Assert.True(broker.IsConnected);

        await broker.DisposeAsync();

        Assert.Contains("connect", inner.Events);
        Assert.Contains("dispose", inner.Events);
        Assert.False(inner.IsConnected);
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        var guard = TestPolicy.CreateGuard();
        var inner = new TestBroker();

        Assert.Throws<ArgumentNullException>(() => new PolicyEnforcingBroker(null!, guard));
        Assert.Throws<ArgumentNullException>(() => new PolicyEnforcingBroker(inner, null!));
    }
}
