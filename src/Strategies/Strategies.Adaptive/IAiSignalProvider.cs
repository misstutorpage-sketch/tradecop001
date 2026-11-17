using TradingCore.Models;

namespace Strategies.Adaptive;

/// <summary>Everything an external signal provider would need to make a decision.</summary>
public sealed record MarketSnapshot(
    string Symbol,
    MarketRegime Regime,
    AggregatedSignal LocalSignal,
    IReadOnlyList<Bar> RecentBars,
    OpenPosition? Position,
    decimal? UnrealizedPnLPercent);

/// <summary>
/// Optional seam for a future AI/model-backed signal source. Nothing is called
/// unless options.AiEnabled is true and a real provider is injected. The default
/// implementation is inert, so the shipped app makes no network calls.
/// </summary>
public interface IAiSignalProvider
{
    string Name { get; }
    Task<Signal?> EvaluateAsync(MarketSnapshot snapshot, CancellationToken ct = default);
}

public sealed class NullAiSignalProvider : IAiSignalProvider
{
    public string Name => "None (local only)";

    public Task<Signal?> EvaluateAsync(MarketSnapshot snapshot, CancellationToken ct = default)
        => Task.FromResult<Signal?>(null);
}
