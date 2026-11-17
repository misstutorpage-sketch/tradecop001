using TradingCore.Abstractions;
using TradingCore.Models;

namespace TradeCop001.Licensing;

/// <summary>
/// Decorator that wraps whichever broker adapter was selected and enforces the
/// signed policy on every order. The strategy still sees a plain IBrokerAdapter.
/// </summary>
public sealed class PolicyEnforcingBroker : BrokerAdapterBase
{
    private readonly IBrokerAdapter _inner;
    private readonly PolicyGuard _policy;

    public PolicyEnforcingBroker(IBrokerAdapter inner, PolicyGuard policy)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _inner.OrderUpdated += (_, result) => RaiseOrderUpdated(result);
    }

    public override string Name => $"{_inner.Name} [policy enforced]";

    public override bool IsConnected
    {
        get => _inner.IsConnected;
        protected set { /* the wrapped adapter owns connection state */ }
    }

    public override Task ConnectAsync(CancellationToken ct = default) => _inner.ConnectAsync(ct);
    public override Task DisconnectAsync(CancellationToken ct = default) => _inner.DisconnectAsync(ct);
    public override Task<AccountInfo> GetAccountAsync(CancellationToken ct = default) => _inner.GetAccountAsync(ct);
    public override Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default) => _inner.GetPositionsAsync(ct);

    public override Task<IReadOnlyList<Bar>> GetHistoricalBarsAsync(
        Symbol symbol, BarInterval interval, int count, CancellationToken ct = default)
        => _inner.GetHistoricalBarsAsync(symbol, interval, count, ct);

    public override async Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        _policy.EnsureOrderAllowed(request.Symbol.Value, request.Quantity);

        var account = await _inner.GetAccountAsync(ct).ConfigureAwait(false);
        _policy.CheckEquity(account.Equity);

        return await _inner.PlaceOrderAsync(request, ct).ConfigureAwait(false);
    }

    public override Task CancelOrderAsync(string brokerOrderId, CancellationToken ct = default)
        => _inner.CancelOrderAsync(brokerOrderId, ct);

    public override IAsyncEnumerable<Tick> StreamTicksAsync(Symbol symbol, CancellationToken ct = default)
        => _inner.StreamTicksAsync(symbol, ct);

    public override IAsyncEnumerable<Bar> StreamBarsAsync(
        Symbol symbol, BarInterval interval, CancellationToken ct = default)
        => _inner.StreamBarsAsync(symbol, interval, ct);

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
