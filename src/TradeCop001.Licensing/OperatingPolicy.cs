namespace TradeCop001.Licensing;

/// <summary>
/// Signed rules that control how tradecop001 may operate on a device.
/// The operator can read this file — nothing about it is hidden.
/// </summary>
public sealed class OperatingPolicy
{
    public string PolicyId { get; set; } = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    public DateTimeOffset EffectiveFrom { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; } = DateTimeOffset.UtcNow.AddYears(1);

    public List<string> AllowedBrokerTypes { get; set; } = new() { "paper" };
    public List<string> AllowedSymbols { get; set; } = new();

    public decimal MaxOrderQuantity { get; set; } = 1m;
    public decimal MaxDailyLoss { get; set; } = 1_000m;

    public bool TradingEnabled { get; set; } = true;
    public TimeOnly? StartUtc { get; set; }
    public TimeOnly? EndUtc { get; set; }

    /// <summary>SHA-256 hex of the control token. Empty disables the control API.</summary>
    public string ControlTokenHash { get; set; } = "";

    /// <summary>Requires the control.remote license feature to bind beyond loopback.</summary>
    public bool AllowRemoteControl { get; set; }
    public string Notes { get; set; } = "";
}
