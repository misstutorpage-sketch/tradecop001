using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradeCop001.Licensing;

/// <summary>Loads and enforces a signed operating policy, plus live pause/resume state.</summary>
public sealed class PolicyGuard
{
    private readonly string _publicKey;
    private readonly string _policyPath;
    private decimal? _baselineEquity;
    private volatile bool _paused;

    public PolicyGuard(string publicKey, string policyPath)
    {
        _publicKey = publicKey;
        _policyPath = policyPath;
    }

    public OperatingPolicy Policy { get; private set; } = new();

    public OperatingPolicy LoadAndValidate()
    {
        var json = Environment.GetEnvironmentVariable("TRADECOP_POLICY");
        if (string.IsNullOrWhiteSpace(json) && File.Exists(_policyPath))
            json = File.ReadAllText(_policyPath);

        if (string.IsNullOrWhiteSpace(json))
            return Policy = new OperatingPolicy { Notes = "No policy file; defaults applied." };

        var envelope = JsonSerializer.Deserialize<SignedEnvelope>(json)
                       ?? throw new InvalidOperationException("Empty policy envelope.");
        var policy = Signing.Verify<OperatingPolicy>(envelope, _publicKey);

        if (policy.EffectiveFrom > DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Policy is not effective yet.");
        if (policy.ExpiresAt < DateTimeOffset.UtcNow)
            throw new InvalidOperationException("Policy has expired.");
        if (policy.MaxOrderQuantity <= 0m)
            throw new InvalidOperationException("Policy MaxOrderQuantity must be positive.");

        return Policy = policy;
    }

    public void EnsureBrokerAllowed(string brokerType)
    {
        if (!Policy.AllowedBrokerTypes.Contains(brokerType, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Policy does not allow broker type '{brokerType}'.");
    }

    public void EnsureOrderAllowed(string symbol, decimal quantity)
    {
        if (_paused || !Policy.TradingEnabled)
            throw new InvalidOperationException("Trading is currently disabled by policy/operator.");

        if (!IsWithinTradingWindow(TimeOnly.FromDateTime(DateTime.UtcNow)))
            throw new InvalidOperationException("Outside the policy's allowed UTC trading window.");

        if (Policy.AllowedSymbols.Count > 0 &&
            !Policy.AllowedSymbols.Contains(symbol, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Policy does not allow symbol '{symbol}'.");

        if (quantity <= 0m || quantity > Policy.MaxOrderQuantity)
            throw new InvalidOperationException(
                $"Order quantity {quantity} violates policy maximum {Policy.MaxOrderQuantity}.");
    }

    public void CheckEquity(decimal currentEquity)
    {
        _baselineEquity ??= currentEquity;
        var loss = _baselineEquity.Value - currentEquity;
        if (loss > Policy.MaxDailyLoss)
            throw new InvalidOperationException(
                $"Daily loss limit of {Policy.MaxDailyLoss} exceeded (current {loss}). Trading halted.");
    }

    public bool IsWithinTradingWindow(TimeOnly now)
    {
        if (Policy.StartUtc is null || Policy.EndUtc is null)
            return true;
        var start = Policy.StartUtc.Value;
        var end = Policy.EndUtc.Value;
        return start <= end ? now >= start && now <= end : now >= start || now <= end;
    }

    public bool TokenMatches(string? token)
    {
        if (string.IsNullOrWhiteSpace(Policy.ControlTokenHash) || string.IsNullOrEmpty(token))
            return false;

        var actual = Signing.HashToken(token);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(actual),
            Encoding.UTF8.GetBytes(Policy.ControlTokenHash));
    }

    public void Pause() => _paused = true;
    public void Resume() => _paused = false;
    public bool IsPaused => _paused;
}
