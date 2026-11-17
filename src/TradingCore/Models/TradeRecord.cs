namespace TradingCore.Models;

/// <summary>
/// A completed round-trip recorded by the strategy. Context bars are the market
/// data that led to the entry, kept so the morphic engine can be reviewed and
/// tuned later. The ledger is local-only; nothing is uploaded.
/// </summary>
public sealed record TradeRecord(
    string Symbol,
    string Direction,
    DateTimeOffset EntryTime,
    DateTimeOffset ExitTime,
    decimal EntryPrice,
    decimal ExitPrice,
    decimal Quantity,
    decimal PnlPercent,
    string ExitReason,
    string EntryMode,
    string EntryRisk,
    string ExitMode,
    string ExitRisk,
    string Regime,
    decimal SignalStrength,
    string SignalReason,
    IReadOnlyList<Bar> EntryContext,
    IReadOnlyList<Bar> ExitContext);
