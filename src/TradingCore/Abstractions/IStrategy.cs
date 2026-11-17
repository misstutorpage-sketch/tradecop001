using TradingCore.Models;

namespace TradingCore.Abstractions;

/// <summary>
/// What a strategy is given at runtime. It can trade without knowing which
/// broker is plugged in — that is the polymorphism point of the whole design.
/// </summary>
public interface IStrategyContext
{
    IBrokerAdapter Broker { get; }
    void Log(string message);

    Task<OrderResult> MarketOrderAsync(
        Symbol symbol,
        OrderSide side,
        decimal quantity,
        string? tag = null,
        CancellationToken ct = default);
}

public sealed class StrategyContext : IStrategyContext
{
    private readonly Action<string> _log;

    public StrategyContext(IBrokerAdapter broker, Action<string> log)
    {
        Broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public IBrokerAdapter Broker { get; }
    public void Log(string message) => _log(message);

    public Task<OrderResult> MarketOrderAsync(
        Symbol symbol,
        OrderSide side,
        decimal quantity,
        string? tag = null,
        CancellationToken ct = default)
        => Broker.PlaceOrderAsync(new OrderRequest(
            Symbol: symbol,
            Side: side,
            Type: OrderType.Market,
            Quantity: quantity,
            ClientOrderId: $"{Guid.NewGuid():N}",
            Tag: tag), ct);
}

/// <summary>
/// Implement this to write your own algorithm. The engine feeds it bars;
/// it trades through <see cref="IStrategyContext"/>.
/// </summary>
public interface IStrategy
{
    string Name { get; }
    IReadOnlyList<Symbol> Subscriptions { get; }
    BarInterval Interval { get; }

    /// <summary>How many historical bars to replay before going live.</summary>
    int RequiredHistoryBars { get; }

    void OnStarted(IStrategyContext context);

    /// <summary>Called once historical replay finishes; enable live trading here.</summary>
    void OnWarmupComplete(IStrategyContext context) { }

    /// <summary>Called for each bar. Async so strategies can place orders without blocking a thread.</summary>
    Task OnBarAsync(Bar bar, IStrategyContext context, CancellationToken ct = default);

    void OnTick(Tick tick, IStrategyContext context) { }
    void OnStopped(IStrategyContext context) { }
}
