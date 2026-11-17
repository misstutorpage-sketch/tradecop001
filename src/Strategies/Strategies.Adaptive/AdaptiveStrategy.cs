using TradingCore.Abstractions;
using TradingCore.Models;

namespace Strategies.Adaptive;

public sealed record AdaptiveStatus(
    string Mode,
    string Risk,
    string Regime,
    string SignalDirection,
    decimal SignalStrength,
    string SignalReason,
    string PositionDirection,
    decimal? UnrealizedPnLPercent,
    int CooldownRemaining,
    string LastAction,
    string AiProvider,
    AdaptiveOptions Options);

/// <summary>
/// Adaptive strategy: combines trend, momentum and breakout signals, re-weights
/// them by detected market regime, and manages entries/exits with hard stops.
/// It stays broker-agnostic — orders go through IStrategyContext only.
/// </summary>
public sealed class AdaptiveStrategy : IStrategy
{
    private readonly object _sync = new();
    private readonly SignalAggregator _aggregator;
    private readonly PositionManager _positions = new();
    private readonly IAiSignalProvider _aiProvider;
    private readonly List<Bar> _bars;
    private readonly int _maxBars;

    private AdaptiveOptions _options;
    private bool _live;
    private AggregatedSignal _lastSignal = AggregatedSignal.Flat(MarketRegime.Ranging, Array.Empty<Signal>());
    private string _lastAction = "none";
    private EntryContext? _entry;

    private sealed record EntryContext(
        DateTimeOffset Time,
        decimal Price,
        decimal Quantity,
        SignalDirection Direction,
        AdaptiveOptions Options,
        AggregatedSignal Signal,
        IReadOnlyList<Bar> Context);

    /// <summary>Raised after a position is closed. A local ledger may persist it.</summary>
    public event EventHandler<TradeRecord>? TradeCompleted;

    public AdaptiveStrategy(AdaptiveOptions options, IAiSignalProvider? aiProvider = null)
    {
        AdaptivePresets.Validate(options);
        _options = options.Clone();
        _aggregator = new SignalAggregator();
        _aiProvider = aiProvider ?? new NullAiSignalProvider();
        _maxBars = Math.Max(200, Math.Max(options.SlowPeriod, options.BreakoutPeriod) * 4);
        _bars = new List<Bar>(_maxBars);
    }

    public string Name => "Adaptive Morph";
    public BarInterval Interval => Snapshot().Interval;
    public int RequiredHistoryBars
    {
        get
        {
            var options = Snapshot();
            return Math.Max(options.SlowPeriod, Math.Max(options.AtrPeriod, options.BreakoutPeriod)) + 2;
        }
    }
    IReadOnlyList<Symbol> IStrategy.Subscriptions => new[] { new Symbol(Snapshot().Symbol) };

    public SignalAggregator Aggregator => _aggregator;
    public PositionManager Positions => _positions;
    public IAiSignalProvider AiProvider => _aiProvider;

    public void OnStarted(IStrategyContext context)
    {
        Reset();
        var options = Snapshot();
        context.Log($"{Name} started: {options.Symbol} mode={options.Mode} risk={options.Risk}");
    }

    public void OnWarmupComplete(IStrategyContext context)
    {
        _live = true;
        context.Log("Warmup complete — adaptive signals enabled.");
    }

    public async Task OnBarAsync(Bar bar, IStrategyContext context, CancellationToken ct = default)
    {
        AddBar(bar);
        if (!_live || _bars.Count < RequiredHistoryBars)
            return;

        var options = Snapshot();
        var signal = _aggregator.Aggregate(_bars, options);

        if (options.AiEnabled && _aiProvider is not NullAiSignalProvider)
        {
            var snapshot = new MarketSnapshot(
                options.Symbol, signal.Regime, signal, _bars.ToArray(),
                _positions.Position, _positions.UnrealizedPnLPercent);
            var aiSignal = await _aiProvider.EvaluateAsync(snapshot, ct).ConfigureAwait(false);
            if (aiSignal is not null && aiSignal.Strength > 0m)
                signal = Blend(signal, aiSignal);
        }

        _lastSignal = signal;
        var action = _positions.Decide(bar, options, signal);
        if (action.Kind != TradeActionKind.Hold)
            context.Log($"Signal {signal.Direction} {signal.Strength:F2} [{signal.Regime}] -> {action.Kind}: {action.Reason}");

        switch (action.Kind)
        {
            case TradeActionKind.OpenLong:
            case TradeActionKind.OpenShort:
                var side = action.Kind == TradeActionKind.OpenLong ? OrderSide.Buy : OrderSide.Sell;
                var result = await TryOrderAsync(context, bar.Symbol, side, options.BaseQuantity,
                    $"{options.Mode}/{options.Risk}: {action.Reason}", ct).ConfigureAwait(false);
                if (result is not null)
                {
                    var filled = result.FilledQuantity > 0m ? result.FilledQuantity : options.BaseQuantity;
                    _positions.ApplyOpen(signal.Direction, filled, result.AverageFillPrice, _bars.Count - 1);
                    _entry = new EntryContext(
                        bar.OpenTime,
                        result.AverageFillPrice,
                        filled,
                        signal.Direction,
                        options,
                        signal,
                        SnapshotContext());
                    _lastAction = $"{action.Kind} @ {result.AverageFillPrice:F5}";
                }
                break;

            case TradeActionKind.Close:
                var position = _positions.Position;
                if (position is not null)
                {
                    var closeSide = position.Direction == SignalDirection.Buy ? OrderSide.Sell : OrderSide.Buy;
                    var closeResult = await TryOrderAsync(context, bar.Symbol, closeSide, position.Quantity,
                        $"{options.Mode}/{options.Risk}: {action.Reason}", ct).ConfigureAwait(false);
                    if (closeResult is not null)
                    {
                        var entry = _entry;
                        var exitPrice = closeResult.AverageFillPrice > 0m
                            ? closeResult.AverageFillPrice
                            : bar.Close;
                        _positions.ApplyClose(options.CooldownBars);
                        _entry = null;
                        _lastAction = $"Close ({action.Reason})";

                        if (entry is not null)
                            RecordCompletedTrade(context, bar, options, entry, exitPrice, action.Reason);
                    }
                }
                break;
        }
    }

    public void OnStopped(IStrategyContext context) => context.Log($"{Name} stopped.");

    public AdaptiveStatus GetStatus()
    {
        var options = Snapshot();
        return new AdaptiveStatus(
            options.Mode.ToString(),
            options.Risk.ToString(),
            _lastSignal.Regime.ToString(),
            _lastSignal.Direction.ToString(),
            _lastSignal.Strength,
            _lastSignal.Signals.Count == 0 ? "warming up" : string.Join(" | ", _lastSignal.Signals.Select(s => $"{s.Strategy}:{s.Direction} {s.Reason}")),
            _positions.Position?.Direction.ToString() ?? "Flat",
            _positions.UnrealizedPnLPercent,
            _positions.CooldownRemaining,
            _lastAction,
            _aiProvider.Name,
            options);
    }

    public AdaptiveOptions GetOptions() => Snapshot();

    public void UpdateOptions(AdaptiveOptions options)
    {
        AdaptivePresets.Validate(options);
        lock (_sync)
            _options = options.Clone();
    }

    public void SetMode(TradingMode mode)
    {
        AdaptiveOptions current;
        lock (_sync) current = _options.Clone();
        UpdateOptions(AdaptivePresets.WithMode(current, mode));
    }

    public void SetRisk(RiskProfile risk)
    {
        AdaptiveOptions current;
        lock (_sync) current = _options.Clone();
        UpdateOptions(AdaptivePresets.WithRisk(current, risk));
    }

    public void Reset()
    {
        lock (_sync)
        {
            _bars.Clear();
            _live = false;
            _lastSignal = AggregatedSignal.Flat(MarketRegime.Ranging, Array.Empty<Signal>());
            _lastAction = "none";
            _entry = null;
        }
        _positions.Reset();
    }

    private async Task<OrderResult?> TryOrderAsync(
        IStrategyContext context, Symbol symbol, OrderSide side, decimal quantity, string tag, CancellationToken ct)
    {
        try
        {
            var result = await context.MarketOrderAsync(symbol, side, quantity, tag, ct).ConfigureAwait(false);
            if (result.Status is OrderStatus.Filled or OrderStatus.Submitted or OrderStatus.New)
                return result;
            context.Log($"Order not accepted ({result.Status}): {result.Message}");
        }
        catch (Exception ex)
        {
            // Policy rejects and venue errors must never kill the engine loop.
            context.Log($"Order blocked: {ex.Message}");
        }
        return null;
    }

    private void AddBar(Bar bar)
    {
        lock (_sync)
        {
            _bars.Add(bar);
            while (_bars.Count > _maxBars)
                _bars.RemoveAt(0);
        }
    }

    private AdaptiveOptions Snapshot()
    {
        lock (_sync)
            return _options.Clone();
    }

    private static AggregatedSignal Blend(AggregatedSignal local, Signal ai)
    {
        // 50/50 blend of local and model views; model strength acts as its confidence.
        var net = (local.Direction == SignalDirection.Buy ? local.Strength : -local.Strength) * 0.5m
                  + (ai.Direction == SignalDirection.Buy ? ai.Strength : -ai.Strength) * 0.5m;
        var direction = net > 0m ? SignalDirection.Buy : net < 0m ? SignalDirection.Sell : SignalDirection.Flat;
        var signals = local.Signals.Append(ai).ToArray();
        return new AggregatedSignal(direction, Math.Abs(net), local.Regime, signals);
    }

    private void RecordCompletedTrade(
        IStrategyContext context,
        Bar exitBar,
        AdaptiveOptions exitOptions,
        EntryContext entry,
        decimal exitPrice,
        string exitReason)
    {
        if (entry.Price <= 0m || exitPrice <= 0m)
            return;

        var pnlPercent = entry.Direction == SignalDirection.Buy
            ? (exitPrice - entry.Price) / entry.Price * 100m
            : (entry.Price - exitPrice) / entry.Price * 100m;

        var record = new TradeRecord(
            exitBar.Symbol.Value,
            entry.Direction.ToString(),
            entry.Time,
            exitBar.OpenTime,
            entry.Price,
            exitPrice,
            entry.Quantity,
            pnlPercent,
            exitReason,
            entry.Options.Mode.ToString(),
            entry.Options.Risk.ToString(),
            exitOptions.Mode.ToString(),
            exitOptions.Risk.ToString(),
            entry.Signal.Regime.ToString(),
            entry.Signal.Strength,
            string.Join(" | ", entry.Signal.Signals.Select(s => $"{s.Strategy}:{s.Direction} {s.Reason}")),
            entry.Context,
            SnapshotContext());

        try
        {
            TradeCompleted?.Invoke(this, record);
        }
        catch (Exception ex)
        {
            // Learning must never break trading.
            context.Log($"Learning record failed: {ex.Message}");
        }
    }

    /// <summary>Last 50 bars are enough context to review a decision without huge files.</summary>
    private IReadOnlyList<Bar> SnapshotContext()
    {
        lock (_sync)
            return _bars.TakeLast(50).ToArray();
    }
}
