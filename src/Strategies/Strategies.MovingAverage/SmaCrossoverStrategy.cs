using TradingCore.Abstractions;
using TradingCore.Models;

namespace Strategies.MovingAverage;

public sealed class SmaCrossoverOptions
{
    public string Symbol { get; set; } = "BTCUSDT";
    public int FastPeriod { get; set; } = 10;
    public int SlowPeriod { get; set; } = 30;
    public decimal Quantity { get; set; } = 0.01m;
}

/// <summary>
/// Example algorithm. It knows nothing about brokers — the exact same class
/// runs against paper, crypto, IB or MT5.
/// </summary>
public sealed class SmaCrossoverStrategy : IStrategy
{
    private readonly SmaCrossoverOptions _options;
    private readonly Queue<decimal> _closes;
    private decimal? _previousSpread;
    private bool _isLong;
    private bool _live;

    public SmaCrossoverStrategy(SmaCrossoverOptions options)
    {
        if (options.FastPeriod >= options.SlowPeriod)
            throw new ArgumentException("FastPeriod must be smaller than SlowPeriod.");

        _options = options;
        _closes = new Queue<decimal>(options.SlowPeriod + 1);
    }

    public string Name => "SMA Crossover";
    public BarInterval Interval => BarInterval.Minute1;
    public int RequiredHistoryBars => _options.SlowPeriod + 2;

    IReadOnlyList<Symbol> IStrategy.Subscriptions => new[] { new Symbol(_options.Symbol) };

    public void OnStarted(IStrategyContext context)
        => context.Log($"{Name} started: {_options.Symbol} fast={_options.FastPeriod} slow={_options.SlowPeriod}");

    public void OnWarmupComplete(IStrategyContext context)
    {
        _live = true;
        context.Log("Warmup complete — live signals enabled.");
    }

    public async Task OnBarAsync(Bar bar, IStrategyContext context, CancellationToken ct = default)
    {
        _closes.Enqueue(bar.Close);
        while (_closes.Count > _options.SlowPeriod + 1)
            _closes.Dequeue();

        if (_closes.Count < _options.SlowPeriod)
            return;

        var values = _closes.ToArray();
        var fast = values.TakeLast(_options.FastPeriod).Average();
        var slow = values.TakeLast(_options.SlowPeriod).Average();
        var spread = fast - slow;

        if (_previousSpread.HasValue && _live)
        {
            if (_previousSpread.Value <= 0m && spread > 0m && !_isLong)
            {
                var result = await context.MarketOrderAsync(
                    bar.Symbol, OrderSide.Buy, _options.Quantity, "SMA golden cross", ct).ConfigureAwait(false);
                _isLong = result.Status is OrderStatus.Filled or OrderStatus.Submitted or OrderStatus.New;
                context.Log($"BUY {_options.Quantity} {bar.Symbol} @ {bar.Close} -> {result.BrokerOrderId}");
            }
            else if (_previousSpread.Value >= 0m && spread < 0m && _isLong)
            {
                var result = await context.MarketOrderAsync(
                    bar.Symbol, OrderSide.Sell, _options.Quantity, "SMA death cross", ct).ConfigureAwait(false);
                _isLong = false;
                context.Log($"SELL {_options.Quantity} {bar.Symbol} @ {bar.Close} -> {result.BrokerOrderId}");
            }
        }

        _previousSpread = spread;
    }

    public void OnStopped(IStrategyContext context) => context.Log($"{Name} stopped.");
}
