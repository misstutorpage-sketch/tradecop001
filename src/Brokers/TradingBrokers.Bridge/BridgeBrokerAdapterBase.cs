using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using TradingCore.Abstractions;
using TradingCore.Configuration;
using TradingCore.Models;

namespace TradingBrokers.Bridge;

/// <summary>
/// Implements IBrokerAdapter on top of the JSON-RPC bridge. Concrete brokers
/// (IB, MT5) only supply a name, host/port and interval mapping.
/// </summary>
public abstract class BridgeBrokerAdapterBase : BrokerAdapterBase
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly JsonRpcBridgeClient _client;
    private readonly ConcurrentDictionary<string, Channel<Bar>> _barChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Channel<Tick>> _tickChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _orderSymbols = new(StringComparer.OrdinalIgnoreCase);

    protected BridgeBrokerAdapterBase(BrokerConfig config)
    {
        var host = config.Get("BridgeHost", "127.0.0.1");
        var port = int.TryParse(config.Get("BridgePort"), out var p) ? p : DefaultPort;
        _client = new JsonRpcBridgeClient(host, port);
        _client.Notification += OnNotification;
    }

    protected abstract int DefaultPort { get; }

    public override async Task ConnectAsync(CancellationToken ct = default)
    {
        await _client.ConnectAsync(ct).ConfigureAwait(false);
        IsConnected = true;
    }

    public override async Task DisconnectAsync(CancellationToken ct = default)
    {
        await _client.DisconnectAsync(ct).ConfigureAwait(false);
        IsConnected = false;
    }

    public override async Task<AccountInfo> GetAccountAsync(CancellationToken ct = default)
    {
        var result = await _client.CallAsync("account", null, ct).ConfigureAwait(false);
        return Deserialize<AccountInfo>(result);
    }

    public override async Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default)
    {
        var result = await _client.CallAsync("positions", null, ct).ConfigureAwait(false);
        return Deserialize<List<Position>>(result);
    }

    public override async Task<IReadOnlyList<Bar>> GetHistoricalBarsAsync(
        Symbol symbol, BarInterval interval, int count, CancellationToken ct = default)
    {
        var result = await _client.CallAsync("history", new
        {
            symbol = symbol.Value,
            interval = MapInterval(interval),
            count
        }, ct).ConfigureAwait(false);

        return Deserialize<List<Bar>>(result);
    }

    public override async Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        var result = await _client.CallAsync("placeOrder", new
        {
            clientOrderId = request.ClientOrderId,
            symbol = request.Symbol.Value,
            side = request.Side.ToString(),
            type = request.Type.ToString(),
            quantity = request.Quantity,
            limitPrice = request.LimitPrice,
            stopPrice = request.StopPrice,
            timeInForce = request.TimeInForce.ToString(),
            tag = request.Tag
        }, ct).ConfigureAwait(false);

        var order = Deserialize<OrderResult>(result);
        _orderSymbols[order.BrokerOrderId] = request.Symbol.Value;
        RaiseOrderUpdated(order);
        return order;
    }

    public override async Task CancelOrderAsync(string brokerOrderId, CancellationToken ct = default)
    {
        _orderSymbols.TryGetValue(brokerOrderId, out var symbol);
        await _client.CallAsync("cancelOrder", new { orderId = brokerOrderId, symbol }, ct).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<Tick> StreamTicksAsync(
        Symbol symbol, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var key = $"tick|{symbol.Value}";
        var channel = _tickChannels.GetOrAdd(key, _ => NewChannel<Tick>());

        await _client.CallAsync("subscribeTicks", new { symbol = symbol.Value }, ct).ConfigureAwait(false);

        await foreach (var tick in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return tick;
    }

    public override async IAsyncEnumerable<Bar> StreamBarsAsync(
        Symbol symbol, BarInterval interval, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var key = $"bar|{symbol.Value}|{interval}";
        var channel = _barChannels.GetOrAdd(key, _ => NewChannel<Bar>());

        await _client.CallAsync("subscribeBars", new
        {
            symbol = symbol.Value,
            interval = MapInterval(interval)
        }, ct).ConfigureAwait(false);

        await foreach (var bar in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return bar;
    }

    public override async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
    }

    protected abstract string MapInterval(BarInterval interval);

    private void OnNotification(string op, JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Undefined)
            return;

        try
        {
            switch (op)
            {
                case "orderUpdate":
                    RaiseOrderUpdated(Deserialize<OrderResult>(result));
                    break;

                case "bar":
                    var symbolValue = result.GetProperty("symbol").GetString() ?? "";
                    var interval = result.TryGetProperty("interval", out var i) ? i.GetString() ?? "" : "";
                    var bar = Deserialize<Bar>(result.GetProperty("bar"));
                    var barKey = $"bar|{symbolValue}|{interval}";
                    if (_barChannels.TryGetValue(barKey, out var barChannel))
                        barChannel.Writer.TryWrite(bar);
                    break;

                case "tick":
                    var tickSymbol = result.GetProperty("symbol").GetString() ?? "";
                    var tick = Deserialize<Tick>(result.GetProperty("tick"));
                    var tickKey = $"tick|{tickSymbol}";
                    if (_tickChannels.TryGetValue(tickKey, out var tickChannel))
                        tickChannel.Writer.TryWrite(tick);
                    break;
            }
        }
        catch
        {
            // Malformed bridge notification — ignore rather than kill the feed.
        }
    }

    private static T Deserialize<T>(JsonElement element)
        => JsonSerializer.Deserialize<T>(element.GetRawText(), Json)
           ?? throw new InvalidOperationException($"Bridge returned an empty {typeof(T).Name}.");

    private static Channel<T> NewChannel<T>() =>
        Channel.CreateBounded<T>(new BoundedChannelOptions(1_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });
}
