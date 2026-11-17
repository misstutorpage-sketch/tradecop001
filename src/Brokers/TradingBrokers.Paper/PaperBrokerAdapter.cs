using System.Collections.Concurrent;
using System.Threading.Channels;
using TradingCore.Abstractions;
using TradingCore.Configuration;
using TradingCore.Models;

namespace TradingBrokers.Paper;

/// <summary>
/// A complete in-memory broker with synthetic prices, fills, positions and account.
/// Use it to validate strategy logic before connecting real capital.
/// </summary>
public sealed class PaperBrokerAdapter : BrokerAdapterBase
{
    public override string Name => "Paper Simulator";

    private readonly ConcurrentDictionary<string, decimal> _midPrices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Channel<Bar>> _barChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Channel<Tick>> _tickChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Position> _positions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, OrderResult> _orders = new(StringComparer.OrdinalIgnoreCase);

    private readonly decimal _startingCash;
    private readonly decimal _spreadPercent;
    private readonly int _barIntervalMs;
    private readonly int _seed;
    private readonly Random _rng;
    private readonly object _sync = new();
    private readonly object _rngLock = new();

    private CancellationTokenSource? _feedCts;
    private Task? _feedTask;
    private decimal _cash;

    public PaperBrokerAdapter(BrokerConfig config)
    {
        _startingCash = decimal.TryParse(config.Get("StartingCash", "100000"), out var cash) ? cash : 100_000m;
        _spreadPercent = decimal.TryParse(config.Get("SpreadPercent", "0.02"), out var spread) ? spread : 0.02m;
        _barIntervalMs = int.TryParse(config.Get("BarIntervalMs", "1000"), out var ms) ? ms : 1_000;
        _seed = int.TryParse(config.Get("Seed", "42"), out var seed) ? seed : 42;

        _cash = _startingCash;
        _rng = new Random(_seed);

        var symbols = config.Get("Symbols", "BTCUSDT,EURUSD")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var symbol in symbols)
        {
            var start = symbol.Contains("BTC", StringComparison.OrdinalIgnoreCase) ? 60_000m : 1.10m;
            _midPrices[symbol] = start;
            _barChannels[symbol] = Channel.CreateBounded<Bar>(new BoundedChannelOptions(2_000)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });
            _tickChannels[symbol] = Channel.CreateBounded<Tick>(new BoundedChannelOptions(2_000)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            });
        }
    }

    public override Task ConnectAsync(CancellationToken ct = default)
    {
        if (IsConnected) return Task.CompletedTask;

        _feedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _feedTask = Task.Run(() => FeedLoopAsync(_feedCts.Token), _feedCts.Token);
        IsConnected = true;
        return Task.CompletedTask;
    }

    public override async Task DisconnectAsync(CancellationToken ct = default)
    {
        _feedCts?.Cancel();
        if (_feedTask is not null)
        {
            try { await _feedTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* ignore */ }
        }
        _feedCts?.Dispose();
        _feedCts = null;
        _feedTask = null;
        IsConnected = false;
    }

    public override Task<AccountInfo> GetAccountAsync(CancellationToken ct = default)
    {
        lock (_sync)
        {
            var unrealized = _positions.Values.Sum(p => p.UnrealizedPnl);
            return Task.FromResult(new AccountInfo(
                AccountId: "PAPER-001",
                Equity: _cash + unrealized,
                Cash: _cash,
                BuyingPower: _cash * 4m,
                Currency: "USD"));
        }
    }

    public override Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Position>>(_positions.Values.ToArray());

    public override Task<IReadOnlyList<Bar>> GetHistoricalBarsAsync(
        Symbol symbol, BarInterval interval, int count, CancellationToken ct = default)
    {
        var bars = new List<Bar>(count);
        var price = GetStartingPrice(symbol.Value);
        var step = IntervalToStep(interval);
        var openTime = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(step * count);

        for (var i = 0; i < count; i++)
        {
            var (o, h, l, c) = NextBar(price);
            bars.Add(new Bar(symbol, openTime + TimeSpan.FromMinutes(step * i), o, h, l, c, _rng.Next(10, 500)));
            price = c;
        }

        return Task.FromResult<IReadOnlyList<Bar>>(bars);
    }

    public override Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        var mid = GetMid(request.Symbol.Value);
        var halfSpread = mid * _spreadPercent / 100m / 2m;
        var fillPrice = request.Type == OrderType.Limit && request.LimitPrice.HasValue
            ? request.LimitPrice.Value
            : request.Side == OrderSide.Buy ? mid + halfSpread : mid - halfSpread;

        lock (_sync)
        {
            var id = $"PAPER-{Interlocked.Increment(ref _orderSeq):D6}";
            var signedQty = request.Side == OrderSide.Buy ? request.Quantity : -request.Quantity;

            _positions.TryGetValue(request.Symbol.Value, out var existing);
            var oldQty = existing?.Quantity ?? 0m;
            var newQty = oldQty + signedQty;
            var newAvg = newQty == 0m
                ? 0m
                : Math.Abs(oldQty) < 0.00000001m
                    ? fillPrice
                    : Math.Abs(newQty) < 0.00000001m
                        ? 0m
                        : ((oldQty * (existing?.AverageEntryPrice ?? 0m)) + (signedQty * fillPrice)) / newQty;

            _positions[request.Symbol.Value] = new Position(
                request.Symbol,
                newQty,
                Math.Abs(newAvg),
                mid);

            _cash -= signedQty * fillPrice;

            var result = new OrderResult(id, request.ClientOrderId, OrderStatus.Filled, request.Quantity, fillPrice);
            _orders[id] = result;
            RaiseOrderUpdated(result);
            return Task.FromResult(result);
        }
    }

    private int _orderSeq;

    public override Task CancelOrderAsync(string brokerOrderId, CancellationToken ct = default)
    {
        if (_orders.TryGetValue(brokerOrderId, out var original))
        {
            var cancelled = original with { Status = OrderStatus.Cancelled };
            _orders[brokerOrderId] = cancelled;
            RaiseOrderUpdated(cancelled);
        }
        return Task.CompletedTask;
    }

    public override IAsyncEnumerable<Tick> StreamTicksAsync(Symbol symbol, CancellationToken ct = default)
        => _tickChannels.TryGetValue(symbol.Value, out var channel)
            ? channel.Reader.ReadAllAsync(ct)
            : EmptyAsync<Tick>();

    public override IAsyncEnumerable<Bar> StreamBarsAsync(
        Symbol symbol, BarInterval interval, CancellationToken ct = default)
        => _barChannels.TryGetValue(symbol.Value, out var channel)
            ? channel.Reader.ReadAllAsync(ct)
            : EmptyAsync<Bar>();

    private static async IAsyncEnumerable<T> EmptyAsync<T>()
    {
        await Task.CompletedTask;
        yield break;
    }

    public override async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private async Task FeedLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_barIntervalMs));

        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            foreach (var (name, oldPrice) in _midPrices)
            {
                var symbol = new Symbol(name);
                var (o, h, l, c) = NextBar(oldPrice);
                var now = DateTimeOffset.UtcNow;
                var bar = new Bar(symbol, now, o, h, l, c, _rng.Next(10, 500));

                var spread = c * _spreadPercent / 100m;
                var tick = new Tick(symbol, now, c - spread / 2m, c + spread / 2m, c);

                _midPrices[name] = c;

                foreach (var key in _positions.Keys.Where(k => k.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    _positions[key] = _positions[key] with { MarketPrice = c };
                }

                _barChannels[name].Writer.TryWrite(bar);
                _tickChannels[name].Writer.TryWrite(tick);
            }
        }
    }

    private (decimal O, decimal H, decimal L, decimal C) NextBar(decimal previousClose)
    {
        lock (_rngLock)
        {
        var change = (decimal)(_rng.NextDouble() - 0.5) * previousClose * 0.004m;
        var open = previousClose;
        var close = Math.Max(0.0001m, open + change);
        var wick = Math.Abs(change) + previousClose * 0.0005m * (decimal)_rng.NextDouble();
        var high = Math.Max(open, close) + wick;
        var low = Math.Min(open, close) - wick;
        return (open, high, Math.Max(0.00001m, low), close);
        }
    }

    private decimal GetMid(string symbol)
        => _midPrices.TryGetValue(symbol, out var price) ? price : GetStartingPrice(symbol);

    private static decimal GetStartingPrice(string symbol)
        => symbol.Contains("BTC", StringComparison.OrdinalIgnoreCase) ? 60_000m : 1.10m;

    private static int IntervalToStep(BarInterval interval) => interval switch
    {
        BarInterval.Minute1 => 1,
        BarInterval.Minute5 => 5,
        BarInterval.Minute15 => 15,
        BarInterval.Hour1 => 60,
        BarInterval.Day1 => 1_440,
        _ => 1
    };
}
