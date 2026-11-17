using TradingCore.Models;

namespace TradingCore.Abstractions;

/// <summary>
/// The single seam between your strategy and any trading venue.
/// Every broker (paper, Binance, IB, MT5, ...) implements this interface.
/// Nothing in the strategy layer references a concrete broker type.
/// </summary>
public interface IBrokerAdapter : IAsyncDisposable
{
    string Name { get; }
    bool IsConnected { get; }

    Task ConnectAsync(CancellationToken ct = default);
    Task DisconnectAsync(CancellationToken ct = default);

    Task<AccountInfo> GetAccountAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<Bar>> GetHistoricalBarsAsync(
        Symbol symbol,
        BarInterval interval,
        int count,
        CancellationToken ct = default);

    Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default);
    Task CancelOrderAsync(string brokerOrderId, CancellationToken ct = default);

    IAsyncEnumerable<Tick> StreamTicksAsync(Symbol symbol, CancellationToken ct = default);
    IAsyncEnumerable<Bar> StreamBarsAsync(Symbol symbol, BarInterval interval, CancellationToken ct = default);

    /// <summary>Fill/cancel/reject notifications pushed by the venue.</summary>
    event EventHandler<OrderResult>? OrderUpdated;
}

/// <summary>Small convenience base class to remove boilerplate from adapters.</summary>
public abstract class BrokerAdapterBase : IBrokerAdapter
{
    public abstract string Name { get; }
    public virtual bool IsConnected { get; protected set; }

    public event EventHandler<OrderResult>? OrderUpdated;

    protected void RaiseOrderUpdated(OrderResult result) => OrderUpdated?.Invoke(this, result);

    public abstract Task ConnectAsync(CancellationToken ct = default);
    public abstract Task DisconnectAsync(CancellationToken ct = default);
    public abstract Task<AccountInfo> GetAccountAsync(CancellationToken ct = default);
    public abstract Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default);

    public abstract Task<IReadOnlyList<Bar>> GetHistoricalBarsAsync(
        Symbol symbol, BarInterval interval, int count, CancellationToken ct = default);

    public abstract Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default);
    public abstract Task CancelOrderAsync(string brokerOrderId, CancellationToken ct = default);

    public abstract IAsyncEnumerable<Tick> StreamTicksAsync(Symbol symbol, CancellationToken ct = default);
    public abstract IAsyncEnumerable<Bar> StreamBarsAsync(Symbol symbol, BarInterval interval, CancellationToken ct = default);

    public virtual ValueTask DisposeAsync()
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }
}
