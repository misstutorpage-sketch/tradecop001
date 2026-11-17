using System.Text.Json;
using TradeCop001.Licensing;

namespace TradeCop001.Tests;

public class PolicyGuardTests
{
    private static PolicyGuard MakeGuard(OperatingPolicy policy)
    {
        var (priv, pub) = Signing.GenerateKeys();
        var env = Signing.Sign(policy, priv);
        var path = Path.Combine(Path.GetTempPath(), $"policy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(env));

        Environment.SetEnvironmentVariable("TRADECOP_POLICY", null);
        var guard = new PolicyGuard(pub, path);
        guard.LoadAndValidate();
        File.Delete(path);
        return guard;
    }

    private static OperatingPolicy ValidPolicy() => new()
    {
        PolicyId = "TESTPOL",
        EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        AllowedBrokerTypes = new List<string> { "paper" },
        AllowedSymbols = new List<string> { "BTCUSDT" },
        MaxOrderQuantity = 0.05m,
        MaxDailyLoss = 100m,
        TradingEnabled = true,
        ControlTokenHash = Signing.HashToken("secret")
    };

    [Fact]
    public void LoadAndValidate_AcceptsValidPolicy()
    {
        var guard = MakeGuard(ValidPolicy());

        Assert.Equal("TESTPOL", guard.Policy.PolicyId);
    }

    [Fact]
    public void LoadAndValidate_RejectsExpiredPolicy()
    {
        var policy = ValidPolicy();
        policy.ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1);
        var (priv, pub) = Signing.GenerateKeys();
        var env = Signing.Sign(policy, priv);
        var path = Path.Combine(Path.GetTempPath(), $"policy-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(env));

        var guard = new PolicyGuard(pub, path);

        Assert.Throws<InvalidOperationException>(() => guard.LoadAndValidate());
        File.Delete(path);
    }

    [Fact]
    public void LoadAndValidate_RejectsNonPositiveMaxQuantity()
    {
        var policy = ValidPolicy();
        policy.MaxOrderQuantity = 0m;

        Assert.Throws<InvalidOperationException>(() => MakeGuard(policy));
    }

    [Fact]
    public void EnsureBrokerAllowed_EnforcesList()
    {
        var guard = MakeGuard(ValidPolicy());

        Assert.Null(Record.Exception(() => guard.EnsureBrokerAllowed("paper")));
        Assert.Throws<InvalidOperationException>(() => guard.EnsureBrokerAllowed("crypto"));
    }

    [Fact]
    public void EnsureOrderAllowed_EnforcesQuantityAndSymbol()
    {
        var guard = MakeGuard(ValidPolicy());

        Assert.Null(Record.Exception(() => guard.EnsureOrderAllowed("BTCUSDT", 0.01m)));
        Assert.Throws<InvalidOperationException>(() => guard.EnsureOrderAllowed("BTCUSDT", 1m));
        Assert.Throws<InvalidOperationException>(() => guard.EnsureOrderAllowed("BTCUSDT", 0m));
        Assert.Throws<InvalidOperationException>(() => guard.EnsureOrderAllowed("EURUSD", 0.01m));
    }

    [Fact]
    public void EnsureOrderAllowed_EnforcesPauseAndDisable()
    {
        var guard = MakeGuard(ValidPolicy());

        guard.Pause();
        Assert.Throws<InvalidOperationException>(() => guard.EnsureOrderAllowed("BTCUSDT", 0.01m));
        Assert.True(guard.IsPaused);

        guard.Resume();
        Assert.Null(Record.Exception(() => guard.EnsureOrderAllowed("BTCUSDT", 0.01m)));

        guard.Policy.TradingEnabled = false;
        Assert.Throws<InvalidOperationException>(() => guard.EnsureOrderAllowed("BTCUSDT", 0.01m));
    }

    [Fact]
    public void IsWithinTradingWindow_HandlesSameDayAndOvernight()
    {
        var policy = ValidPolicy();
        policy.StartUtc = new TimeOnly(8, 0);
        policy.EndUtc = new TimeOnly(20, 0);
        var dayGuard = MakeGuard(policy);

        Assert.True(dayGuard.IsWithinTradingWindow(new TimeOnly(9, 0)));
        Assert.False(dayGuard.IsWithinTradingWindow(new TimeOnly(21, 0)));

        var overnight = ValidPolicy();
        overnight.StartUtc = new TimeOnly(22, 0);
        overnight.EndUtc = new TimeOnly(6, 0);
        var nightGuard = MakeGuard(overnight);

        Assert.True(nightGuard.IsWithinTradingWindow(new TimeOnly(23, 0)));
        Assert.True(nightGuard.IsWithinTradingWindow(new TimeOnly(3, 0)));
        Assert.False(nightGuard.IsWithinTradingWindow(new TimeOnly(12, 0)));
    }

    [Fact]
    public void CheckEquity_EnforcesDailyLoss()
    {
        var guard = MakeGuard(ValidPolicy());

        guard.CheckEquity(1_000m);          // baseline
        Assert.Null(Record.Exception(() => guard.CheckEquity(950m)));  // loss 50
        Assert.Throws<InvalidOperationException>(() => guard.CheckEquity(850m)); // loss 150 > 100
    }

    [Fact]
    public void TokenMatches_EnforcesHash()
    {
        var guard = MakeGuard(ValidPolicy());

        Assert.True(guard.TokenMatches("secret"));
        Assert.False(guard.TokenMatches("wrong"));
        Assert.False(guard.TokenMatches(null));
    }
}
