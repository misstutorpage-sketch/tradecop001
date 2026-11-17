using TradingCore.Abstractions;
using TradingCore.Models;

namespace TradingCore.Engine;

/// <summary>One order attempt as seen on the canonical (strategy) side.</summary>
public sealed record JournalEntry(
    DateTimeOffset Timestamp,
    string Symbol,
    OrderSide Side,
    OrderType Type,
    decimal Quantity,
    OrderStatus Status,
    decimal FilledQuantity,
    decimal AverageFillPrice,
    string? BrokerOrderId,
    string? Tag)
{
    public bool IsFill => Status is OrderStatus.Filled or OrderStatus.PartiallyFilled;
}

/// <summary>
/// In-memory record of every order that reached the venue, used by the daily
/// progress report. Nothing is sent anywhere and nothing is written to disk
/// unless the operator asks for it.
/// </summary>
public sealed class SessionJournal
{
    private readonly object _sync = new();
    private readonly List<JournalEntry> _entries = new();
    private readonly int _capacity;

    public SessionJournal(int capacity = 5_000)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count
    {
        get { lock (_sync) return _entries.Count; }
    }

    public void Record(OrderRequest request, OrderResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        var entry = new JournalEntry(
            DateTimeOffset.Now,
            request.Symbol.Value,
            request.Side,
            request.Type,
            request.Quantity,
            result.Status,
            result.FilledQuantity,
            result.AverageFillPrice,
            result.BrokerOrderId,
            request.Tag);

        lock (_sync)
        {
            _entries.Add(entry);
            while (_entries.Count > _capacity)
                _entries.RemoveAt(0);
        }
    }

    /// <summary>Entries recorded at or after the given instant, oldest first.</summary>
    public IReadOnlyList<JournalEntry> Since(DateTimeOffset from)
    {
        lock (_sync)
            return _entries.Where(entry => entry.Timestamp >= from).ToArray();
    }

    public IReadOnlyList<JournalEntry> All()
    {
        lock (_sync)
            return _entries.ToArray();
    }

    public void Clear()
    {
        lock (_sync)
            _entries.Clear();
    }
}

/// <summary>
/// Thin decorator that records accepted order attempts after policy checks.
/// It never changes orders or results.
/// </summary>
public sealed class JournalingBrokerAdapter : BrokerAdapterBase
{
    private readonly IBrokerAdapter _inner;
    private readonly SessionJournal _journal;

    public JournalingBrokerAdapter(IBrokerAdapter inner, SessionJournal journal)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _inner.OrderUpdated += (_, result) => RaiseOrderUpdated(result);
    }

    public override string Name => $"{_inner.Name} [journal]";

    public override bool IsConnected
    {
        get => _inner.IsConnected;
        protected set { /* the wrapped adapter owns connection state */ }
    }

    public override Task ConnectAsync(CancellationToken ct = default) => _inner.ConnectAsync(ct);
    public override Task DisconnectAsync(CancellationToken ct = default) => _inner.DisconnectAsync(ct);
    public override Task<AccountInfo> GetAccountAsync(CancellationToken ct = default) => _inner.GetAccountAsync(ct);
    public override Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default)
        => _inner.GetPositionsAsync(ct);

    public override Task<IReadOnlyList<Bar>> GetHistoricalBarsAsync(
        Symbol symbol, BarInterval interval, int count, CancellationToken ct = default)
        => _inner.GetHistoricalBarsAsync(symbol, interval, count, ct);

    public override async Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        var result = await _inner.PlaceOrderAsync(request, ct).ConfigureAwait(false);
        _journal.Record(request, result);
        return result;
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
