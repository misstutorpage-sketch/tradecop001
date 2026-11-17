using System.Collections.Concurrent;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using TradingCore.Abstractions;
using TradingCore.Configuration;
using TradingCore.Models;

namespace TradingBrokers.Crypto;

/// <summary>
/// REST + WebSocket adapter using the Binance spot API shape. Because most crypto
/// exchanges expose a similar surface, pointing this at another venue is mostly a
/// matter of overriding the URL/path settings (or subclassing for different auth).
///
/// Note: ccxt has no first-class .NET build, so this adapter speaks the native
/// protocol directly. If you prefer ccxt semantics, wrap it behind IBrokerAdapter
/// the same way — the rest of the app will not notice.
/// </summary>
public class CryptoRestBrokerAdapter : BrokerAdapterBase
{
    public override string Name => "Crypto REST";

    private readonly HttpClient _http = new();
    private readonly string _restBase;
    private readonly string _wsBase;
    private readonly string _apiKey;
    private readonly string _apiSecret;
    private readonly long _recvWindow;

    private readonly ConcurrentDictionary<string, Channel<Bar>> _barChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Channel<Tick>> _tickChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task> _pumps = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _orderSymbols = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource _lifetime = new();

    public CryptoRestBrokerAdapter(BrokerConfig config)
    {
        var credentials = config.ToCredentials();
        _restBase = config.Get("BaseRestUrl", "https://api.binance.com");
        _wsBase = config.Get("BaseWsUrl", "wss://stream.binance.com:9443/ws");
        _apiKey = credentials.ApiKey;
        _apiSecret = credentials.ApiSecret;
        _recvWindow = long.TryParse(config.Get("RecvWindow", "5000"), out var rw) ? rw : 5_000;
    }

    public override Task ConnectAsync(CancellationToken ct = default)
    {
        _lifetime.Dispose();
        _lifetime = new CancellationTokenSource();
        IsConnected = true;
        return Task.CompletedTask;
    }

    public override Task DisconnectAsync(CancellationToken ct = default)
    {
        _lifetime.Cancel();
        IsConnected = false;
        return Task.CompletedTask;
    }

    public override async Task<AccountInfo> GetAccountAsync(CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Get, "/api/v3/account", "", signed: true, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var balances = doc.RootElement.GetProperty("balances");

        decimal cash = 0m;
        decimal locked = 0m;
        foreach (var item in balances.EnumerateArray())
        {
            if (!string.Equals(item.GetProperty("asset").GetString(), "USDT", StringComparison.OrdinalIgnoreCase))
                continue;
            cash = ParseDecimal(item.GetProperty("free"));
            locked = ParseDecimal(item.GetProperty("locked"));
        }

        return new AccountInfo(
            AccountId: string.IsNullOrEmpty(_apiKey) ? "crypto-demo" : $"crypto-{_apiKey[..Math.Min(6, _apiKey.Length)]}",
            Equity: cash + locked,
            Cash: cash,
            BuyingPower: cash,
            Currency: "USDT");
    }

    public override async Task<IReadOnlyList<Position>> GetPositionsAsync(CancellationToken ct = default)
    {
        var json = await SendAsync(HttpMethod.Get, "/api/v3/account", "", signed: true, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var result = new List<Position>();

        foreach (var item in doc.RootElement.GetProperty("balances").EnumerateArray())
        {
            var asset = item.GetProperty("asset").GetString() ?? "";
            var free = ParseDecimal(item.GetProperty("free"));
            var locked = ParseDecimal(item.GetProperty("locked"));
            var total = free + locked;
            if (total <= 0m || asset.Equals("USDT", StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(new Position(new Symbol(asset + "USDT"), total, 0m));
        }

        return result;
    }

    public override async Task<IReadOnlyList<Bar>> GetHistoricalBarsAsync(
        Symbol symbol, BarInterval interval, int count, CancellationToken ct = default)
    {
        var query = $"symbol={Normalize(symbol)}&interval={MapInterval(interval)}&limit={Math.Clamp(count, 1, 1000)}";
        var json = await SendAsync(HttpMethod.Get, "/api/v3/klines", query, signed: false, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var bars = new List<Bar>(count);
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            var openTimeMs = row[0].GetInt64();
            bars.Add(new Bar(
                symbol,
                DateTimeOffset.FromUnixTimeMilliseconds(openTimeMs),
                ParseDecimal(row[1]),
                ParseDecimal(row[2]),
                ParseDecimal(row[3]),
                ParseDecimal(row[4]),
                ParseDecimal(row[5])));
        }
        return bars;
    }

    public override async Task<OrderResult> PlaceOrderAsync(OrderRequest request, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.Append("symbol=").Append(Normalize(request.Symbol));
        sb.Append("&side=").Append(request.Side == OrderSide.Buy ? "BUY" : "SELL");
        sb.Append("&type=").Append(MapOrderType(request.Type));
        sb.Append("&quantity=").Append(request.Quantity.ToString(CultureInfo.InvariantCulture));

        if (request.Type is (OrderType.Limit or OrderType.StopLimit) && request.LimitPrice.HasValue)
            sb.Append("&price=").Append(request.LimitPrice.Value.ToString(CultureInfo.InvariantCulture));
        if (request.Type is (OrderType.Stop or OrderType.StopLimit) && request.StopPrice.HasValue)
        {
            sb.Append("&stopPrice=").Append(request.StopPrice.Value.ToString(CultureInfo.InvariantCulture));
            sb.Append("&type=STOP_LOSS");
        }
        if (request.Type == OrderType.Limit)
            sb.Append("&timeInForce=").Append(MapTimeInForce(request.TimeInForce));
        if (!string.IsNullOrEmpty(request.ClientOrderId))
            sb.Append("&newClientOrderId=").Append(Uri.EscapeDataString(request.ClientOrderId));

        var json = await SendAsync(HttpMethod.Post, "/api/v3/order", sb.ToString(), signed: true, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var result = new OrderResult(
            BrokerOrderId: root.GetProperty("orderId").ToString(),
            ClientOrderId: root.TryGetProperty("clientOrderId", out var cid) ? cid.GetString() : request.ClientOrderId,
            Status: MapStatus(root.TryGetProperty("status", out var s) ? s.GetString() ?? "" : ""),
            FilledQuantity: root.TryGetProperty("executedQty", out var q) ? ParseDecimal(q) : 0m,
            AverageFillPrice: root.TryGetProperty("avgPrice", out var p) ? ParseDecimal(p) : 0m,
            Message: request.Tag);

        _orderSymbols[result.BrokerOrderId] = Normalize(request.Symbol);
        RaiseOrderUpdated(result);
        return result;
    }

    public override async Task CancelOrderAsync(string brokerOrderId, CancellationToken ct = default)
    {
        var symbol = _orderSymbols.TryGetValue(brokerOrderId, out var s) ? s : "BTCUSDT";
        await SendAsync(HttpMethod.Delete, "/api/v3/order", $"symbol={symbol}&orderId={brokerOrderId}",
            signed: true, ct).ConfigureAwait(false);
    }

    public override IAsyncEnumerable<Tick> StreamTicksAsync(Symbol symbol, CancellationToken ct = default)
    {
        var key = $"tick|{Normalize(symbol)}";
        var channel = _tickChannels.GetOrAdd(key, _ => BoundedChannel<Tick>());
        _pumps.GetOrAdd(key, _ => Task.Run(
            () => PumpTickAsync(symbol, channel.Writer, _lifetime.Token),
            _lifetime.Token));
        return channel.Reader.ReadAllAsync(ct);
    }

    public override IAsyncEnumerable<Bar> StreamBarsAsync(
        Symbol symbol, BarInterval interval, CancellationToken ct = default)
    {
        var key = $"bar|{Normalize(symbol)}|{interval}";
        var channel = _barChannels.GetOrAdd(key, _ => BoundedChannel<Bar>());
        _pumps.GetOrAdd(key, _ => Task.Run(
            () => PumpKlineAsync(symbol, interval, channel.Writer, _lifetime.Token),
            _lifetime.Token));
        return channel.Reader.ReadAllAsync(ct);
    }

    public override async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        _http.Dispose();
        await base.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<string> SendAsync(
        HttpMethod method, string path, string query, bool signed, CancellationToken ct)
    {
        if (signed)
        {
            query = string.IsNullOrEmpty(query) ? "" : query + "&";
            query += $"timestamp={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}&recvWindow={_recvWindow}";
            query += "&signature=" + Sign(query);
        }

        var url = _restBase.TrimEnd('/') + path;
        if (!string.IsNullOrEmpty(query))
            url += "?" + query;

        using var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(_apiKey))
            request.Headers.Add("X-MBX-APIKEY", _apiKey);

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Crypto API error {(int)response.StatusCode}: {body}");
        return body;
    }

    private string Sign(string payload)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_apiSecret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private async Task PumpKlineAsync(Symbol symbol, BarInterval interval, ChannelWriter<Bar> writer, CancellationToken ct)
    {
        var stream = $"{Normalize(symbol).ToLowerInvariant()}@kline_{MapInterval(interval)}";
        var (messages, _) = StartWebSocketPump(stream, ct);
        await foreach (var message in messages.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            using var doc = JsonDocument.Parse(message);
            if (!doc.RootElement.TryGetProperty("k", out var k))
                continue;

            writer.TryWrite(new Bar(
                symbol,
                DateTimeOffset.FromUnixTimeMilliseconds(k.GetProperty("t").GetInt64()),
                ParseDecimal(k.GetProperty("o")),
                ParseDecimal(k.GetProperty("h")),
                ParseDecimal(k.GetProperty("l")),
                ParseDecimal(k.GetProperty("c")),
                ParseDecimal(k.GetProperty("v"))));
        }
    }

    private async Task PumpTickAsync(Symbol symbol, ChannelWriter<Tick> writer, CancellationToken ct)
    {
        var stream = $"{Normalize(symbol).ToLowerInvariant()}@ticker";
        var (messages, _) = StartWebSocketPump(stream, ct);
        await foreach (var message in messages.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            writer.TryWrite(new Tick(
                symbol,
                DateTimeOffset.UtcNow,
                ParseDecimal(root.GetProperty("b")),
                ParseDecimal(root.GetProperty("a")),
                ParseDecimal(root.GetProperty("c"))));
        }
    }

    private (Channel<string> Messages, Task Pump) StartWebSocketPump(string stream, CancellationToken ct)
    {
        var messages = Channel.CreateBounded<string>(new BoundedChannelOptions(500)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        var uri = new Uri($"{_wsBase.TrimEnd('/')}/{stream}");

        var pump = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                using var socket = new ClientWebSocket();
                try
                {
                    await socket.ConnectAsync(uri, ct).ConfigureAwait(false);

                    var buffer = new byte[64 * 1024];
                    while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
                    {
                        var text = new StringBuilder();
                        WebSocketReceiveResult frame;
                        do
                        {
                            frame = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                            if (frame.MessageType == WebSocketMessageType.Close)
                                break;
                            text.Append(Encoding.UTF8.GetString(buffer, 0, frame.Count));
                        } while (!frame.EndOfMessage);

                        if (frame.MessageType == WebSocketMessageType.Close)
                            break;

                        messages.Writer.TryWrite(text.ToString());
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }

            messages.Writer.TryComplete();
        }, ct);

        return (messages, pump);
    }

    private static Channel<T> BoundedChannel<T>() =>
        System.Threading.Channels.Channel.CreateBounded<T>(new BoundedChannelOptions(1_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

    private static decimal ParseDecimal(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Number => element.GetDecimal(),
            JsonValueKind.String => decimal.TryParse(
                element.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0m,
            _ => 0m
        };

    private static string Normalize(Symbol symbol)
        => symbol.Value.Replace(":", "", StringComparison.Ordinal).ToUpperInvariant();

    private static string MapInterval(BarInterval interval) => interval switch
    {
        BarInterval.Minute1 => "1m",
        BarInterval.Minute5 => "5m",
        BarInterval.Minute15 => "15m",
        BarInterval.Hour1 => "1h",
        BarInterval.Day1 => "1d",
        _ => "1m"
    };

    private static string MapOrderType(OrderType type) => type switch
    {
        OrderType.Limit => "LIMIT",
        OrderType.Stop => "STOP_LOSS",
        OrderType.StopLimit => "STOP_LOSS_LIMIT",
        _ => "MARKET"
    };

    private static string MapTimeInForce(TimeInForce tif) => tif switch
    {
        TimeInForce.Gtc => "GTC",
        TimeInForce.Ioc => "IOC",
        TimeInForce.Fok => "FOK",
        _ => "GTC"
    };

    private static OrderStatus MapStatus(string status) => status switch
    {
        "NEW" => OrderStatus.New,
        "PARTIALLY_FILLED" => OrderStatus.PartiallyFilled,
        "FILLED" => OrderStatus.Filled,
        "CANCELED" or "CANCELLED" => OrderStatus.Cancelled,
        "REJECTED" => OrderStatus.Rejected,
        "EXPIRED" => OrderStatus.Expired,
        _ => OrderStatus.Submitted
    };
}
