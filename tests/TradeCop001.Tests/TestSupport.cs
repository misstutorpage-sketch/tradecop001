using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using TradeCop001.Licensing;
using TradingCore.Abstractions;
using TradingCore.Models;

namespace TradeCop001.Tests;

/// <summary>Configurable in-memory broker used to observe decorator/engine behaviour.</summary>
public sealed class TestBroker : BrokerAdapterBase
{
    public override string Name => "Test Broker";

    public List<OrderRequest> Orders { get; } = new();
    public List<string> Events { get; } = new();
    public decimal Equity { get; set; } = 100_000m;
    public IReadOnlyList<Bar> History { get; set; } = Array.Empty<Bar>();
    public Channel<Bar> LiveBars { get; } = Channel.CreateUnbounded<Bar>();
    public Func<OrderRequest, OrderResult>? OnOrder { get; set; }

    public override Task ConnectAsync(CancellationToken ct = default)
    {
        Events.Add("connect");
        IsConnected = true;
        return Task.CompletedTask;
    }

    public override Task DisconnectAsync(CancellationToken ct = default)
    {
        Events.Add("disconnect");
        IsConnected = false;
        return Task.CompletedTask;
    }

    public override Task<AccountInfo> GetAccountAsync(CancellationToken ct = default)
        => Task.FromResult(new AccountInfo("TEST-001", Equity, Equity, Equity * 4m, "USD"));

    public override Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Position>>(Array.Empty<Position>());

    public override Task<IReadOnlyList<Bar>> GetHistoricalBarsAsync(
        Symbol symbol, BarInterval interval, int count, CancellationToken ct = default)
        => Task.FromResult(History);

    public override Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        Orders.Add(request);
        var result = OnOrder?.Invoke(request)
            ?? new OrderResult($"TEST-{Orders.Count:D6}", request.ClientOrderId,
                OrderStatus.Filled, request.Quantity, request.Quantity * 100m);
        RaiseOrderUpdated(result);
        return Task.FromResult(result);
    }

    public override Task CancelOrderAsync(string brokerOrderId, CancellationToken ct = default)
    {
        Events.Add($"cancel:{brokerOrderId}");
        return Task.CompletedTask;
    }

    public override async IAsyncEnumerable<Tick> StreamTicksAsync(
        Symbol symbol, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    public override async IAsyncEnumerable<Bar> StreamBarsAsync(
        Symbol symbol, BarInterval interval, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var bar in LiveBars.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return bar;
    }

    public override ValueTask DisposeAsync()
    {
        Events.Add("dispose");
        LiveBars.Writer.TryComplete();
        return base.DisposeAsync();
    }
}

/// <summary>Shared factories for signed policies in tests.</summary>
public static class TestPolicy
{
    public static OperatingPolicy Valid(
        string broker = "paper",
        string symbol = "BTCUSDT",
        decimal maxQty = 0.05m,
        decimal maxDailyLoss = 100m,
        string token = "secret") => new()
        {
            PolicyId = "TESTPOL",
            EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            AllowedBrokerTypes = new List<string> { broker },
            AllowedSymbols = new List<string> { symbol },
            MaxOrderQuantity = maxQty,
            MaxDailyLoss = maxDailyLoss,
            TradingEnabled = true,
            ControlTokenHash = Signing.HashToken(token)
        };

    public static PolicyGuard CreateGuard(OperatingPolicy? policy = null)
    {
        policy ??= Valid();
        var (privateKey, publicKey) = Signing.GenerateKeys();
        var envelope = Signing.Sign(policy, privateKey);
        var path = Path.Combine(Path.GetTempPath(), $"policy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(envelope));

        Environment.SetEnvironmentVariable("TRADECOP_POLICY", null);
        try
        {
            var guard = new PolicyGuard(publicKey, path);
            guard.LoadAndValidate();
            return guard;
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Records engine callbacks without placing trades.</summary>
public sealed class RecordingStrategy : IStrategy
{
    private readonly IReadOnlyList<Symbol> _subscriptions;

    public RecordingStrategy(IReadOnlyList<Symbol> subscriptions, BarInterval interval, int requiredHistory)
    {
        _subscriptions = subscriptions;
        Interval = interval;
        RequiredHistoryBars = requiredHistory;
    }

    public string Name => "Recording Strategy";
    public IReadOnlyList<Symbol> Subscriptions => _subscriptions;
    public BarInterval Interval { get; }
    public int RequiredHistoryBars { get; }

    public List<Bar> Bars { get; } = new();
    public bool Started { get; private set; }
    public bool WarmupCompleted { get; private set; }
    public bool Stopped { get; private set; }
    public CancellationToken LastToken { get; private set; }

    public void OnStarted(IStrategyContext context) => Started = true;
    public void OnWarmupComplete(IStrategyContext context) => WarmupCompleted = true;

    public Task OnBarAsync(Bar bar, IStrategyContext context, CancellationToken ct = default)
    {
        Bars.Add(bar);
        LastToken = ct;
        return Task.CompletedTask;
    }

    public void OnStopped(IStrategyContext context) => Stopped = true;
}

/// <summary>Minimal strategy context that records log lines and uses the supplied broker.</summary>
public sealed class RecordingContext : IStrategyContext
{
    public RecordingContext(IBrokerAdapter broker) => Broker = broker;

    public IBrokerAdapter Broker { get; }
    public List<string> Logs { get; } = new();
    public void Log(string message) => Logs.Add(message);

    public Task<OrderResult> MarketOrderAsync(Symbol symbol, OrderSide side, decimal quantity,
        string? tag = null, CancellationToken ct = default)
        => Broker.PlaceOrderAsync(new OrderRequest(symbol, side, OrderType.Market, quantity,
            ClientOrderId: Guid.NewGuid().ToString("N"), Tag: tag), ct);
}
