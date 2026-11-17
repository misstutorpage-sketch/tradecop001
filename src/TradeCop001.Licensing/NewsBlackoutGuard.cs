using TradingCore.Abstractions;

namespace TradeCop001.Licensing;

/// <summary>
/// Blocks new orders around high-impact events. This is a risk control, not a
/// prediction: it reduces exposure during spikes without needing to know direction.
/// Disabled by default and inert with NullNewsProvider.
/// </summary>
public sealed class NewsBlackoutGuard
{
    private readonly INewsProvider _provider;
    private readonly int _minutesBefore;
    private readonly int _minutesAfter;

    public NewsBlackoutGuard(
        INewsProvider provider,
        int minutesBefore = 15,
        int minutesAfter = 15)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _minutesBefore = Math.Max(0, minutesBefore);
        _minutesAfter = Math.Max(0, minutesAfter);
    }

    public bool Enabled { get; set; }

    public string ProviderName => _provider.Name;

    public async Task EnsureTradingAllowedAsync(CancellationToken ct = default)
    {
        if (!Enabled)
            return;

        var snapshot = await _provider.GetSnapshotAsync(ct).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var blocking = snapshot.UpcomingEvents.FirstOrDefault(e =>
            e.Impact.Equals("High", StringComparison.OrdinalIgnoreCase) &&
            now >= e.TimeUtc.AddMinutes(-_minutesBefore) &&
            now <= e.TimeUtc.AddMinutes(_minutesAfter));

        if (blocking is not null)
            throw new InvalidOperationException(
                $"News blackout active: {blocking.Title} ({blocking.Currency}) at {blocking.TimeUtc:u}.");
    }
}
