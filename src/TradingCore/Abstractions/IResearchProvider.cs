namespace TradingCore.Abstractions;

/// <summary>A scheduled event that can move a market (NFP, CPI, rate decision, ...).</summary>
public sealed record EconomicEvent(
    string Id,
    DateTimeOffset TimeUtc,
    string Currency,
    string Impact,
    string Title);

/// <summary>What a research provider knows right now.</summary>
public sealed record ResearchSnapshot(
    IReadOnlyList<EconomicEvent> UpcomingEvents,
    decimal? MarketSentiment = null);

/// <summary>
/// Opt-in seam for news and world-event research. Nothing is called unless a real
/// provider is explicitly configured; the shipped default makes no network calls.
/// </summary>
public interface INewsProvider
{
    string Name { get; }
    Task<ResearchSnapshot> GetSnapshotAsync(CancellationToken ct = default);
}

public sealed class NullNewsProvider : INewsProvider
{
    public string Name => "None (offline)";

    public Task<ResearchSnapshot> GetSnapshotAsync(CancellationToken ct = default)
        => Task.FromResult(new ResearchSnapshot(Array.Empty<EconomicEvent>()));
}
