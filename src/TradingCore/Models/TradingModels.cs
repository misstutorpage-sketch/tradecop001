namespace TradingCore.Models;

/// <summary>Direction of an order.</summary>
public enum OrderSide
{
    Buy,
    Sell
}

/// <summary>Order execution style. Brokers map these to their own order types.</summary>
public enum OrderType
{
    Market,
    Limit,
    Stop,
    StopLimit
}

public enum TimeInForce
{
    Day,
    Gtc,
    Ioc,
    Fok
}

public enum OrderStatus
{
    New,
    Submitted,
    PartiallyFilled,
    Filled,
    Cancelled,
    Rejected,
    Expired
}

/// <summary>Bar sizes the engine understands. Each adapter maps these to broker-native intervals.</summary>
public enum BarInterval
{
    Tick,
    Minute1,
    Minute5,
    Minute15,
    Hour1,
    Day1
}

public sealed record Symbol(string Value, string? Exchange = null)
{
    public override string ToString() => Exchange is null
        ? Value
        : $"{Exchange}:{Value}";
}

/// <summary>A single OHLCV candle, broker-agnostic.</summary>
public sealed record Bar(
    Symbol Symbol,
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

/// <summary>A best-bid/offer snapshot.</summary>
public sealed record Tick(
    Symbol Symbol,
    DateTimeOffset Time,
    decimal Bid,
    decimal Ask,
    decimal? Last = null,
    decimal? LastVolume = null)
{
    public decimal Mid => (Bid + Ask) / 2m;
    public decimal Spread => Ask - Bid;
}

/// <summary>What a strategy asks a broker to do. No broker-specific fields leak in here.</summary>
public sealed record OrderRequest(
    Symbol Symbol,
    OrderSide Side,
    OrderType Type,
    decimal Quantity,
    decimal? LimitPrice = null,
    decimal? StopPrice = null,
    TimeInForce TimeInForce = TimeInForce.Day,
    string? ClientOrderId = null,
    string? Tag = null);

/// <summary>Standardised result returned by every broker adapter.</summary>
public sealed record OrderResult(
    string BrokerOrderId,
    string? ClientOrderId,
    OrderStatus Status,
    decimal FilledQuantity,
    decimal AverageFillPrice,
    string? Message = null);

/// <summary>Signed quantity: positive = long, negative = short.</summary>
public sealed record Position(
    Symbol Symbol,
    decimal Quantity,
    decimal AverageEntryPrice,
    decimal? MarketPrice = null)
{
    public decimal UnrealizedPnl => MarketPrice is null
        ? 0m
        : (MarketPrice.Value - AverageEntryPrice) * Quantity;
}

public sealed record AccountInfo(
    string AccountId,
    decimal Equity,
    decimal Cash,
    decimal BuyingPower,
    string Currency);

/// <summary>Credentials passed to an adapter from configuration (never hard-coded).</summary>
public sealed record BrokerCredentials(
    string ApiKey = "",
    string ApiSecret = "",
    string? Passphrase = null,
    string? AccountId = null);
