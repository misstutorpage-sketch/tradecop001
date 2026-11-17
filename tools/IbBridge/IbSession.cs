using System.Collections.Concurrent;
using System.Globalization;
using IBApi;

namespace IbBridge;

/// <summary>
/// Owns the TWS/Gateway connection and translates IB callbacks into JSON-RPC
/// results and notifications. All broker state lives here, not in the TCP layer.
/// </summary>
public sealed class IbSession : DefaultEWrapper
{
    private const int PositionsRequestId = 0;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly BridgeOptions _options;
    private readonly EReaderMonitorSignal _signal = new();
    private readonly EClientSocket _client;
    private readonly EReader _reader;
    private readonly ConcurrentDictionary<int, Waiter> _waiters = new();
    private readonly ConcurrentDictionary<int, Subscription> _subscriptions = new();
    private readonly ConcurrentDictionary<int, string> _orderClients = new();
    private readonly ConcurrentDictionary<int, BidAsk> _quotes = new();
    private readonly TaskCompletionSource<int> _nextOrderId = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<string> _managedAccounts = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _nextRequestId = 1;
    private int _orderIdSeed;
    private string _account = "";
    private CancellationTokenSource? _readerLifetime;

    public IbSession(BridgeOptions options)
    {
        _options = options;
        _client = new EClientSocket(this, _signal);
        _reader = new EReader(_client, _signal);
    }

    public event Action<string, object>? Notification;

    public bool IsConnected => _client.IsConnected();

    public string Account => _account;

    public async Task ConnectAsync(CancellationToken ct)
    {
        _client.eConnect(_options.TwsHost, _options.TwsPort, _options.ClientId);
        _reader.Start();
        _readerLifetime = new CancellationTokenSource();
        _ = Task.Run(() => ReaderLoopAsync(_readerLifetime.Token));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        var firstOrderId = await _nextOrderId.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        if (firstOrderId > _orderIdSeed)
            Interlocked.Exchange(ref _orderIdSeed, firstOrderId - 1);

        var accounts = await _managedAccounts.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        _account = PaperOnlyGuard.SelectAccount(_options.AccountId, accounts, _options.AllowLiveTrading);

        // Delayed market data keeps paper accounts usable without data subscriptions.
        _client.reqMarketDataType(3);
    }

    public async Task<AccountInfoDto> GetAccountAsync(CancellationToken ct)
    {
        var id = NextRequestId();
        var waiter = AddWaiter(id, "account");
        _client.reqAccountSummary(id, _account, "NetLiquidation,TotalCashValue,BuyingPower,Currency");
        return await WaitAsync<AccountInfoDto>(id, waiter, ct).ConfigureAwait(false);
    }

    public async Task<List<PositionDto>> GetPositionsAsync(CancellationToken ct)
    {
        var waiter = AddWaiter(PositionsRequestId, "positions");
        _client.reqPositions();
        return await WaitAsync<List<PositionDto>>(PositionsRequestId, waiter, ct).ConfigureAwait(false);
    }

    public async Task<List<BarDto>> GetHistoryAsync(string symbol, string interval, int count, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("history requires 'symbol'.", nameof(symbol));

        var take = Math.Clamp(count, 1, 5_000);
        var contract = ContractFor(symbol, null);
        var id = NextRequestId();
        var waiter = AddWaiter(id, "history", symbol, interval, take);

        _client.reqHistoricalData(
            id, contract, "", DurationFor(interval, take), interval, WhatToShow(contract),
            useRTH: 0, formatDate: 2, keepUpToDate: false, chartOptions: null!);

        var bars = await WaitAsync<List<BarDto>>(id, waiter, ct).ConfigureAwait(false);
        return bars.TakeLast(take).ToList();
    }

    public void SubscribeBars(string symbol, string interval)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("subscribeBars requires 'symbol'.", nameof(symbol));

        var id = NextRequestId();
        _subscriptions[id] = new Subscription(symbol, interval, IsBars: true);

        var contract = ContractFor(symbol, null);
        _client.reqHistoricalData(
            id, contract, "", DurationFor(interval, 1), interval, WhatToShow(contract),
            useRTH: 0, formatDate: 2, keepUpToDate: true, chartOptions: null!);
    }

    public void SubscribeTicks(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("subscribeTicks requires 'symbol'.", nameof(symbol));

        var id = NextRequestId();
        _subscriptions[id] = new Subscription(symbol, "", IsBars: false);
        _quotes[id] = new BidAsk();
        _client.reqMktData(id, ContractFor(symbol, null), "", false, false, mktDataOptions: null!);
    }

    public Task<OrderResultDto> PlaceOrderAsync(ProtocolRequest request, CancellationToken ct)
    {
        PaperOnlyGuard.EnsureTradingAllowed(_account, _options.AllowLiveTrading);

        var symbol = request.GetString("symbol");
        if (string.IsNullOrWhiteSpace(symbol))
            throw new ArgumentException("placeOrder requires 'symbol'.", nameof(request));

        var quantity = request.GetDecimal("quantity");
        if (quantity <= 0)
            throw new ArgumentException("placeOrder requires a positive 'quantity'.", nameof(request));

        var side = request.GetEnum("side", OrderSide.Buy);
        var type = request.GetEnum("type", OrderType.Market);
        var clientOrderId = request.GetString("clientOrderId");
        var orderId = NextOrderId();
        _orderClients[orderId] = clientOrderId;

        var order = new Order
        {
            OrderId = orderId,
            Account = _account,
            Action = side == OrderSide.Buy ? "BUY" : "SELL",
            TotalQuantity = (double)quantity,
            OrderType = IbOrderType(type),
            Tif = IbTimeInForce(request.GetEnum("timeInForce", TimeInForce.Day)),
            Transmit = true,
            OrderRef = clientOrderId
        };

        if (type is OrderType.Limit or OrderType.StopLimit)
            order.LmtPrice = (double)request.GetDecimal("limitPrice");

        if (type is OrderType.Stop or OrderType.StopLimit)
            order.AuxPrice = (double)request.GetDecimal("stopPrice");

        _client.placeOrder(orderId, ContractFor(symbol, request.GetString("exchange")), order);

        return Task.FromResult(new OrderResultDto(
            orderId.ToString(CultureInfo.InvariantCulture),
            clientOrderId,
            "Submitted",
            0m,
            0m,
            "Order transmitted to IB."));
    }

    public Task<OrderResultDto> CancelOrderAsync(string orderId, string? clientOrderId, CancellationToken ct)
    {
        PaperOnlyGuard.EnsureTradingAllowed(_account, _options.AllowLiveTrading);

        if (!int.TryParse(orderId, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            throw new ArgumentException($"cancelOrder 'orderId' must be an integer IB order id, got '{orderId}'.", nameof(orderId));

        _orderClients.TryGetValue(id, out var knownClientId);
        _client.cancelOrder(id);

        return Task.FromResult(new OrderResultDto(
            orderId,
            string.IsNullOrWhiteSpace(clientOrderId) ? knownClientId : clientOrderId,
            "Cancelled",
            0m,
            0m,
            "Cancel request sent to IB."));
    }

    public void CancelSubscriptions()
    {
        foreach (var (id, subscription) in _subscriptions)
        {
            if (subscription.IsBars)
                _client.cancelHistoricalData(id);
            else
                _client.cancelMktData(id);
        }

        _subscriptions.Clear();
        _quotes.Clear();
    }

    public async ValueTask DisconnectAsync()
    {
        CancelSubscriptions();

        foreach (var waiter in _waiters.Values)
            waiter.Fail("IB bridge is shutting down.");
        _waiters.Clear();

        _readerLifetime?.Cancel();
        _signal.issueSignal();

        if (IsConnected)
            _client.eDisconnect(resetState: true);

        await ValueTask.CompletedTask.ConfigureAwait(false);
    }

    public override void nextValidId(int orderId)
    {
        if (orderId > _orderIdSeed)
            Interlocked.Exchange(ref _orderIdSeed, orderId - 1);

        _nextOrderId.TrySetResult(orderId);
    }

    public override void managedAccounts(string accountsList)
        => _managedAccounts.TrySetResult(accountsList);

    public override void accountSummary(int reqId, string account, string tag, string value, string currency)
    {
        if (!_waiters.TryGetValue(reqId, out var waiter) || waiter.Kind != "account")
            return;

        waiter.AccountValues[tag] = value;
        if (tag.Equals("Currency", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(currency))
            waiter.Currency = currency;
        else if (waiter.Currency.Length == 0 && !string.IsNullOrWhiteSpace(currency))
            waiter.Currency = currency;
    }

    public override void accountSummaryEnd(int reqId)
    {
        if (!_waiters.TryGetValue(reqId, out var waiter) || waiter.Kind != "account")
            return;

        decimal Value(params string[] tags)
        {
            foreach (var tag in tags)
            {
                if (waiter.AccountValues.TryGetValue(tag, out var raw) &&
                    decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                    return parsed;
            }

            return 0m;
        }

        var currency = waiter.Currency;
        if (currency.Length == 0 && waiter.AccountValues.TryGetValue("Currency", out var tagCurrency))
            currency = tagCurrency;

        waiter.Completion.TrySetResult(new AccountInfoDto(
            _account,
            Value("NetLiquidation"),
            Value("TotalCashValue", "CashBalance"),
            Value("BuyingPower"),
            currency));
    }

    public override void position(string account, Contract contract, double pos, double avgCost)
    {
        if (!string.Equals(account, _account, StringComparison.OrdinalIgnoreCase))
            return;

        if (!_waiters.TryGetValue(PositionsRequestId, out var waiter) || waiter.Kind != "positions")
            return;

        waiter.Positions.Add(new PositionDto(
            new SymbolDto(contract.Symbol, contract.Exchange),
            (decimal)pos,
            (decimal)avgCost));
    }

    public override void positionEnd()
    {
        if (_waiters.TryGetValue(PositionsRequestId, out var waiter) && waiter.Kind == "positions")
            waiter.Completion.TrySetResult(waiter.Positions);
    }

    public override void historicalData(int reqId, Bar bar)
    {
        if (!_waiters.TryGetValue(reqId, out var waiter) || waiter.Kind != "history")
            return; // Backfill for a subscription; live updates come via historicalDataUpdate.

        waiter.Bars.Add(ToBar(waiter.Symbol!, bar));
    }

    public override void historicalDataEnd(int reqId, string start, string end)
    {
        if (_waiters.TryGetValue(reqId, out var waiter) && waiter.Kind == "history")
            waiter.Completion.TrySetResult(waiter.Bars);
    }

    public override void historicalDataUpdate(int reqId, Bar bar)
    {
        if (!_subscriptions.TryGetValue(reqId, out var subscription) || !subscription.IsBars)
            return;

        Notification?.Invoke("bar", new BarNotification(
            subscription.Symbol,
            subscription.Interval,
            ToBar(subscription.Symbol, bar)));
    }

    public override void tickPrice(int tickerId, int field, double price, TickAttrib attribs)
    {
        if (!_subscriptions.TryGetValue(tickerId, out var subscription) || subscription.IsBars)
            return;

        var quote = _quotes.GetOrAdd(tickerId, _ => new BidAsk());
        if (field == TickType.BID)
            quote.Bid = price;
        else if (field == TickType.ASK)
            quote.Ask = price;
        else
            return;

        quote.Time = DateTimeOffset.UtcNow;
        PublishTick(tickerId, subscription, quote);
    }

    public override void tickString(int tickerId, int field, string value)
    {
        if (field != TickType.LAST_TIMESTAMP || !_quotes.TryGetValue(tickerId, out var quote))
            return;

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            quote.Time = DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    public override void orderStatus(
        int orderId, string status, double filled, double remaining, double avgFillPrice,
        int permId, int parentId, double lastFillPrice, int clientId, string whyHeld, double mktCapPrice)
    {
        _orderClients.TryGetValue(orderId, out var clientOrderId);
        Notification?.Invoke("orderUpdate", new OrderResultDto(
            orderId.ToString(CultureInfo.InvariantCulture),
            clientOrderId,
            MapOrderStatus(status),
            (decimal)filled,
            (decimal)avgFillPrice,
            string.IsNullOrWhiteSpace(whyHeld) ? null : whyHeld));
    }

    public override void error(int id, int errorCode, string errorMsg)
    {
        Console.Error.WriteLine($"[IB] error id={id} code={errorCode}: {errorMsg}");

        if (_waiters.TryRemove(id, out var waiter))
        {
            waiter.Fail($"{errorMsg} (IB error {errorCode})");
            return;
        }

        if (_orderClients.ContainsKey(id))
        {
            _orderClients.TryGetValue(id, out var clientOrderId);
            Notification?.Invoke("orderUpdate", new OrderResultDto(
                id.ToString(CultureInfo.InvariantCulture),
                clientOrderId,
                "Rejected",
                0m,
                0m,
                $"{errorMsg} (IB error {errorCode})"));
        }
    }

    public override void connectionClosed()
    {
        Console.Error.WriteLine("[IB] connection closed.");
        foreach (var waiter in _waiters.Values)
            waiter.Fail("IB connection was closed.");
    }

    private async Task ReaderLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && IsConnected)
        {
            try
            {
                _signal.waitForSignal();
                _reader.processMsgs();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    Console.Error.WriteLine($"[IB] reader stopped: {ex.Message}");
                break;
            }
        }
    }

    private void PublishTick(int tickerId, Subscription subscription, BidAsk quote)
    {
        if (double.IsNaN(quote.Bid) || double.IsNaN(quote.Ask))
            return;

        Notification?.Invoke("tick", new TickNotification(
            subscription.Symbol,
            new TickDto(
                new SymbolDto(subscription.Symbol),
                quote.Time,
                (decimal)quote.Bid,
                (decimal)quote.Ask)));
    }

    private Waiter AddWaiter(int id, string kind, string? symbol = null, string? interval = null, int count = 0)
    {
        var waiter = new Waiter(kind, symbol, interval, count);
        _waiters[id] = waiter;
        return waiter;
    }

    private async Task<T> WaitAsync<T>(int id, Waiter waiter, CancellationToken ct) where T : class
    {
        try
        {
            return (T)await waiter.Completion.Task.WaitAsync(RequestTimeout, ct).ConfigureAwait(false);
        }
        finally
        {
            _waiters.TryRemove(id, out _);
        }
    }

    private int NextRequestId() => Interlocked.Increment(ref _nextRequestId);

    private int NextOrderId() => Interlocked.Increment(ref _orderIdSeed);

    private static Contract ContractFor(string symbol, string? exchange)
    {
        var contract = new Contract { Symbol = symbol };

        if (!string.IsNullOrWhiteSpace(exchange))
        {
            contract.Exchange = exchange;
            contract.SecType = "STK";
            contract.Currency = "USD";
            return contract;
        }

        if (symbol.Length == 6 && symbol.All(char.IsLetter))
        {
            // EURUSD / GBPJPY style forex pair.
            contract.SecType = "CASH";
            contract.Currency = symbol[3..];
            return contract;
        }

        contract.SecType = "STK";
        contract.Exchange = "SMART";
        contract.Currency = "USD";
        return contract;
    }

    private static string WhatToShow(Contract contract)
        => contract.SecType == "CASH" ? "MIDPOINT" : "TRADES";

    private static string DurationFor(string interval, int count)
    {
        var secondsPerBar = interval switch
        {
            "5 mins" => 300,
            "15 mins" => 900,
            "1 hour" => 3_600,
            "1 day" => 86_400,
            _ => 60
        };

        var days = Math.Max(1, (int)Math.Ceiling(count * (double)secondsPerBar / 86_400d));
        return $"{days} D";
    }

    private static string IbOrderType(OrderType type) => type switch
    {
        OrderType.Limit => "LMT",
        OrderType.Stop => "STP",
        OrderType.StopLimit => "STP LMT",
        _ => "MKT"
    };

    private static string IbTimeInForce(TimeInForce tif) => tif switch
    {
        TimeInForce.Gtc => "GTC",
        TimeInForce.Ioc => "IOC",
        TimeInForce.Fok => "FOK",
        _ => "DAY"
    };

    private static string MapOrderStatus(string status)
    {
        if (status.Contains("Cancelled", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Canceled", StringComparison.OrdinalIgnoreCase))
            return "Cancelled";

        return status switch
        {
            "Filled" => "Filled",
            "PartiallyFilled" => "PartiallyFilled",
            "Rejected" => "Rejected",
            "Expired" => "Expired",
            "Submitted" => "Submitted",
            _ => "New"
        };
    }

    private static BarDto ToBar(string symbol, Bar bar)
        => new(
            new SymbolDto(symbol),
            ParseIbTime(bar.Time),
            (decimal)bar.Open,
            (decimal)bar.High,
            (decimal)bar.Low,
            (decimal)bar.Close,
            (decimal)bar.Volume);

    private static DateTimeOffset ParseIbTime(string raw)
    {
        if (long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            return DateTimeOffset.FromUnixTimeSeconds(seconds);

        string[] formats = ["yyyyMMdd  HH:mm:ss", "yyyyMMdd HH:mm:ss", "yyyy-MM-dd HH:mm:ss"];
        foreach (var format in formats)
        {
            if (DateTimeOffset.TryParseExact(raw, format, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return parsed;
        }

        return DateTimeOffset.UtcNow;
    }

    private sealed class Waiter
    {
        public Waiter(string kind, string? symbol, string? interval, int count)
        {
            Kind = kind;
            Symbol = symbol;
            Interval = interval;
            Count = count;
        }

        public string Kind { get; }
        public string? Symbol { get; }
        public string? Interval { get; }
        public int Count { get; }
        public List<BarDto> Bars { get; } = [];
        public List<PositionDto> Positions { get; } = [];
        public Dictionary<string, string> AccountValues { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string Currency { get; set; } = "";
        public TaskCompletionSource<object> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Fail(string message) => Completion.TrySetException(new InvalidOperationException(message));
    }

    private sealed record Subscription(string Symbol, string Interval, bool IsBars);

    private sealed class BidAsk
    {
        public double Bid { get; set; } = double.NaN;
        public double Ask { get; set; } = double.NaN;
        public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;
    }
}
