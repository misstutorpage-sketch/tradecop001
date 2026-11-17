using System.Runtime.CompilerServices;
using TradingCore.Abstractions;
using TradingCore.Models;

namespace TradingCore.Adapters;

/// <summary>
/// Translates canonical symbols to a venue's symbol names and back. This lets the
/// same strategy and policy run on Deriv, Exness or any other MT5 broker unchanged.
/// </summary>
public sealed class MappedBrokerAdapter : BrokerAdapterBase
{
    private readonly IBrokerAdapter _inner;
    private readonly IReadOnlyDictionary<string, string> _map;
    private readonly IReadOnlyDictionary<string, string> _reverse;

    public MappedBrokerAdapter(IBrokerAdapter inner, IReadOnlyDictionary<string, string> symbolMap)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _map = new Dictionary<string, string>(symbolMap ?? throw new ArgumentNullException(nameof(symbolMap)),
            StringComparer.OrdinalIgnoreCase);
        _reverse = _map
            .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.OrdinalIgnoreCase);
        _inner.OrderUpdated += (_, result) => RaiseOrderUpdated(result);
    }

    public override string Name => $"{_inner.Name} [mapped]";

    public override bool IsConnected
    {
        get => _inner.IsConnected;
        protected set { /* inner owns connection state */ }
    }

    public override Task ConnectAsync(CancellationToken ct = default) => _inner.ConnectAsync(ct);
    public override Task DisconnectAsync(CancellationToken ct = default) => _inner.DisconnectAsync(ct);
    public override Task<AccountInfo> GetAccountAsync(CancellationToken ct = default) => _inner.GetAccountAsync(ct);
    public override Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default)
        => _inner.GetPositionsAsync(ct);

    public override async Task<IReadOnlyList<Bar>> GetHistoricalBarsAsync(
        Symbol symbol, BarInterval interval, int count, CancellationToken ct = default)
    {
        var bars = await _inner.GetHistoricalBarsAsync(ToVenue(symbol), interval, count, ct).ConfigureAwait(false);
        return bars.Select(ToCanonical).ToArray();
    }

    public override Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
        => _inner.PlaceOrderAsync(request with { Symbol = ToVenue(request.Symbol) }, ct);

    public override Task CancelOrderAsync(string brokerOrderId, CancellationToken ct = default)
        => _inner.CancelOrderAsync(brokerOrderId, ct);

    public override async IAsyncEnumerable<Tick> StreamTicksAsync(
        Symbol symbol, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var tick in _inner.StreamTicksAsync(ToVenue(symbol), ct).ConfigureAwait(false))
            yield return tick with { Symbol = ToCanonical(tick.Symbol) };
    }

    public override async IAsyncEnumerable<Bar> StreamBarsAsync(
        Symbol symbol, BarInterval interval, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var bar in _inner.StreamBarsAsync(ToVenue(symbol), interval, ct).ConfigureAwait(false))
            yield return ToCanonical(bar);
    }

    public override async ValueTask DisposeAsync()
    {
        await _inner.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    public Symbol ToVenue(Symbol symbol)
        => new(_map.TryGetValue(symbol.Value, out var mapped) ? mapped : symbol.Value, symbol.Exchange);

    public Symbol ToCanonical(Symbol symbol)
        => _reverse.TryGetValue(symbol.Value, out var canonical) ? new Symbol(canonical) : symbol;

    private Bar ToCanonical(Bar bar) => bar with { Symbol = ToCanonical(bar.Symbol) };
}
