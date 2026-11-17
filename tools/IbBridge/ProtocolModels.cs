using System.Text.Json;
using System.Text.Json.Serialization;

namespace IbBridge;

/// <summary>Shared JSON options and envelopes for the newline-delimited protocol.</summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string Success(int id, object result)
        => JsonSerializer.Serialize(new { id, ok = true, result }, Options);

    public static string Error(int id, string message)
        => JsonSerializer.Serialize(new { id, ok = false, error = message }, Options);

    public static string Notification(string op, object result)
        => JsonSerializer.Serialize(new { op, result }, Options);
}

public sealed record SymbolDto(string Value, string? Exchange = null);

public sealed record BarDto(
    SymbolDto Symbol,
    DateTimeOffset OpenTime,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    decimal Volume);

public sealed record TickDto(
    SymbolDto Symbol,
    DateTimeOffset Time,
    decimal Bid,
    decimal Ask,
    decimal? Last = null,
    decimal? LastVolume = null);

public sealed record AccountInfoDto(
    string AccountId,
    decimal Equity,
    decimal Cash,
    decimal BuyingPower,
    string Currency);

public sealed record PositionDto(
    SymbolDto Symbol,
    decimal Quantity,
    decimal AverageEntryPrice,
    decimal? MarketPrice = null);

public sealed record OrderResultDto(
    string BrokerOrderId,
    string? ClientOrderId,
    string Status,
    decimal FilledQuantity,
    decimal AverageFillPrice,
    string? Message = null);

public sealed record BarNotification(string Symbol, string Interval, BarDto Bar);

public sealed record TickNotification(string Symbol, TickDto Tick);

public enum OrderSide
{
    Buy,
    Sell
}

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
