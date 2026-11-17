using Strategies.MovingAverage;
using TradeCop001.Licensing;
using TradingCore.Abstractions;
using TradingCore.Configuration;
using TradingCore.Engine;

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

    public AppRuntime(
        BrokerResolver resolver,
        BrokerConfig brokerConfig,
        SmaCrossoverOptions options,
        PolicyGuard policy,
        Action<string> log)
    {
        _resolver = resolver;
        _brokerConfig = brokerConfig;
        _options = options;
        _policy = policy;
        _log = log;
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
            var broker = new PolicyEnforcingBroker(rawBroker, _policy);
            var strategy = new SmaCrossoverStrategy(GetOptions());
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
            State = "Stopped";
        }
    }
}
