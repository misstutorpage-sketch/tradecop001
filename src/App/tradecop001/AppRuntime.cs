using Strategies.MovingAverage;
using TradeCop001.Licensing;
using TradingCore.Abstractions;
using TradingCore.Adapters;
using TradingCore.Configuration;
using TradingCore.Engine;
using TradingCore.Models;

namespace TradeCop001;

/// <summary>
/// Owns the currently running engine so the control API can start/stop/pause it
/// without restarting the process.
/// </summary>
public sealed class AppRuntime
{
    private readonly BrokerResolver _resolver;
    private readonly BrokerConfig _brokerConfig;
    private readonly PolicyGuard _policy;
    private readonly Action<string> _log;
    private readonly object _sync = new();

    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private SmaCrossoverOptions _options;
    private readonly IStrategy? _strategyOverride;
    private readonly IReadOnlyDictionary<string, string>? _symbolMap;
    private readonly NewsBlackoutGuard? _news;
    private readonly SessionJournal _journal = new();

    private IBrokerAdapter? _activeBroker;

    public AppRuntime(
        BrokerResolver resolver,
        BrokerConfig brokerConfig,
        SmaCrossoverOptions options,
        PolicyGuard policy,
        Action<string> log,
        IReadOnlyDictionary<string, string>? symbolMap = null,
        NewsBlackoutGuard? news = null)
    {
        _resolver = resolver;
        _brokerConfig = brokerConfig;
        _options = options;
        _policy = policy;
        _log = log;
        _symbolMap = symbolMap;
        _news = news;
    }

    /// <summary>Use a pre-built strategy (e.g. the adaptive engine) instead of the default SMA.</summary>
    public AppRuntime(
        BrokerResolver resolver,
        BrokerConfig brokerConfig,
        IStrategy strategy,
        PolicyGuard policy,
        Action<string> log,
        IReadOnlyDictionary<string, string>? symbolMap = null,
        NewsBlackoutGuard? news = null)
    {
        _resolver = resolver;
        _brokerConfig = brokerConfig;
        _strategyOverride = strategy;
        _options = new SmaCrossoverOptions();
        _policy = policy;
        _log = log;
        _symbolMap = symbolMap;
        _news = news;
    }

    public IStrategy? Strategy => _strategyOverride;

    /// <summary>Order history for the daily progress report (canonical symbols).</summary>
    public SessionJournal Journal => _journal;

    public string? ActiveBrokerName
    {
        get { lock (_sync) return _activeBroker?.Name; }
    }

    /// <summary>Best-effort venue account snapshot; never throws into the control API.</summary>
    public async Task<AccountInfo?> TryGetAccountAsync(CancellationToken ct = default)
    {
        IBrokerAdapter? broker;
        lock (_sync) broker = _activeBroker;
        if (broker is null)
            return null;

        try { return await broker.GetAccountAsync(ct).ConfigureAwait(false); }
        catch { return null; }
    }

    /// <summary>Best-effort open positions; returns an empty list if the venue is unavailable.</summary>
    public async Task<IReadOnlyList<Position>> TryGetPositionsAsync(CancellationToken ct = default)
    {
        IBrokerAdapter? broker;
        lock (_sync) broker = _activeBroker;
        if (broker is null)
            return Array.Empty<Position>();

        try { return await broker.GetPositionsAsync(ct).ConfigureAwait(false); }
        catch { return Array.Empty<Position>(); }
    }

    public string State { get; private set; } = "Stopped";
    public string? LastError { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }

    public void Start()
    {
        lock (_sync)
        {
            if (_runTask is not null && !_runTask.IsCompleted)
                return;

            _policy.EnsureBrokerAllowed(_brokerConfig.Type);
            _cts = new CancellationTokenSource();
            StartedAt = DateTimeOffset.UtcNow;
            State = "Running";
            LastError = null;
            _runTask = RunAsync(_cts.Token);
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_sync)
        {
            cts = _cts;
            task = _runTask;
        }

        cts?.Cancel();
        if (task is not null)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch { /* shutdown should not hang */ }
        }

        lock (_sync)
        {
            State = "Stopped";
            _cts = null;
        }
    }

    public SmaCrossoverOptions GetOptions()
    {
        lock (_sync)
        {
            return new SmaCrossoverOptions
            {
                Symbol = _options.Symbol,
                FastPeriod = _options.FastPeriod,
                SlowPeriod = _options.SlowPeriod,
                Quantity = _options.Quantity
            };
        }
    }

    public void UpdateOptions(SmaCrossoverOptions options)
    {
        if (options.FastPeriod >= options.SlowPeriod)
            throw new ArgumentException("FastPeriod must be smaller than SlowPeriod.");
        if (options.Quantity <= 0m)
            throw new ArgumentException("Quantity must be positive.");

        _policy.EnsureOrderAllowed(options.Symbol, options.Quantity);

        lock (_sync)
        {
            _options = new SmaCrossoverOptions
            {
                Symbol = options.Symbol,
                FastPeriod = options.FastPeriod,
                SlowPeriod = options.SlowPeriod,
                Quantity = options.Quantity
            };
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            var rawBroker = _resolver.Resolve(_brokerConfig);

            IBrokerAdapter broker = rawBroker;
            if (_symbolMap is { Count: > 0 })
            {
                broker = new MappedBrokerAdapter(broker, _symbolMap);
                _log($"Symbol mapping active ({_symbolMap.Count} venue symbols).");
            }
            broker = new PolicyEnforcingBroker(broker, _policy, _news);
            broker = new JournalingBrokerAdapter(broker, _journal);

            lock (_sync)
                _activeBroker = broker;

            var strategy = _strategyOverride ?? new SmaCrossoverStrategy(GetOptions());
            IStrategyContext context = new StrategyContext(broker, _log);
            var engine = new TradingEngine(broker, strategy, context);

            await engine.RunAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // expected on stop
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _log($"Engine error: {ex.Message}");
        }
        finally
        {
            lock (_sync)
            {
                _activeBroker = null;
                State = "Stopped";
            }
        }
    }
}
