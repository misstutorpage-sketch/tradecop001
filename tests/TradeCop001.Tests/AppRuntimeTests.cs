using Strategies.MovingAverage;
using TradeCop001;
using TradeCop001.Licensing;
using TradingBrokers.Paper;
using TradingCore.Abstractions;
using TradingCore.Configuration;

namespace TradeCop001.Tests;

public class AppRuntimeTests
{
    private static BrokerResolver MakeResolver() => new(new IBrokerAdapterFactory[]
    {
        new PaperBrokerFactory()
    });

    private static BrokerConfig PaperConfig() => new()
    {
        Type = "paper",
        Settings = new Dictionary<string, string>
        {
            ["Symbols"] = "BTCUSDT",
            ["StartingCash"] = "100000",
            ["Seed"] = "7",
            ["BarIntervalMs"] = "20"
        }
    };

    private static SmaCrossoverOptions Options() => new()
    {
        Symbol = "BTCUSDT", FastPeriod = 3, SlowPeriod = 5, Quantity = 0.01m
    };

    private static AppRuntime MakeRuntime(PolicyGuard? guard = null) => new(
        MakeResolver(), PaperConfig(), Options(), guard ?? TestPolicy.CreateGuard(), _ => { });

    [Fact]
    public async Task StartThenStop_TransitionsState()
    {
        var runtime = MakeRuntime();

        Assert.Equal("Stopped", runtime.State);
        runtime.Start();
        Assert.Equal("Running", runtime.State);
        Assert.NotNull(runtime.StartedAt);

        await runtime.StopAsync();
        Assert.Equal("Stopped", runtime.State);
    }

    [Fact]
    public void Start_RefusesDisallowedBroker()
    {
        var guard = TestPolicy.CreateGuard(TestPolicy.Valid(broker: "crypto"));
        var runtime = MakeRuntime(guard);

        var ex = Assert.Throws<InvalidOperationException>(() => runtime.Start());

        Assert.Contains("does not allow", ex.Message);
        Assert.Equal("Stopped", runtime.State);
    }

    [Fact]
    public void UpdateOptions_AppliesValidChanges()
    {
        var runtime = MakeRuntime();

        runtime.UpdateOptions(new SmaCrossoverOptions
        {
            Symbol = "BTCUSDT", FastPeriod = 4, SlowPeriod = 8, Quantity = 0.02m
        });

        var options = runtime.GetOptions();
        Assert.Equal(4, options.FastPeriod);
        Assert.Equal(8, options.SlowPeriod);
        Assert.Equal(0.02m, options.Quantity);
    }

    [Fact]
    public void UpdateOptions_RejectsPolicyViolation()
    {
        var runtime = MakeRuntime();

        Assert.Throws<InvalidOperationException>(() => runtime.UpdateOptions(new SmaCrossoverOptions
        {
            Symbol = "BTCUSDT", FastPeriod = 3, SlowPeriod = 5, Quantity = 1m
        }));
    }

    [Fact]
    public void UpdateOptions_RejectsInvalidPeriodsAndQuantity()
    {
        var runtime = MakeRuntime();

        Assert.Throws<ArgumentException>(() => runtime.UpdateOptions(new SmaCrossoverOptions
        {
            Symbol = "BTCUSDT", FastPeriod = 10, SlowPeriod = 5, Quantity = 0.01m
        }));
        Assert.Throws<ArgumentException>(() => runtime.UpdateOptions(new SmaCrossoverOptions
        {
            Symbol = "BTCUSDT", FastPeriod = 2, SlowPeriod = 5, Quantity = 0m
        }));
    }

    [Fact]
    public async Task StopAsync_BeforeStart_IsSafe()
    {
        var runtime = MakeRuntime();

        await runtime.StopAsync();

        Assert.Equal("Stopped", runtime.State);
    }
}
