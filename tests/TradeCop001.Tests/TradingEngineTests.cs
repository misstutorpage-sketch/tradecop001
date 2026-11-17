using TradingCore.Engine;
using TradingCore.Models;

namespace TradeCop001.Tests;

public class TradingEngineTests
{
    private static Bar MakeBar(Symbol symbol, int i, decimal price) => new(
        symbol, DateTimeOffset.UtcNow.AddMinutes(i), price, price, price, price, 10m);

    [Fact]
    public async Task Engine_ReplaysHistoryThenLiveBars_AndStopsWhenStreamEnds()
    {
        var symbol = new Symbol("BTCUSDT");
        var history = Enumerable.Range(0, 5).Select(i => MakeBar(symbol, i, 100 + i)).ToList();
        var broker = new TestBroker { History = history };
        var strategy = new RecordingStrategy(new[] { symbol }, BarInterval.Minute1, 5);
        var context = new RecordingContext(broker);
        var engine = new TradingEngine(broker, strategy, context);

        for (var i = 0; i < 3; i++)
            await broker.LiveBars.Writer.WriteAsync(MakeBar(symbol, 10 + i, 200 + i));
        broker.LiveBars.Writer.TryComplete();

        await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(8, strategy.Bars.Count); // 5 replayed + 3 live
        Assert.True(strategy.Started);
        Assert.True(strategy.WarmupCompleted);
        Assert.True(strategy.Stopped);
        Assert.Contains("connect", broker.Events);
        Assert.Contains("disconnect", broker.Events);
        Assert.Contains("dispose", broker.Events);
        Assert.Contains("Engine stopped cleanly.", context.Logs);
    }

    [Fact]
    public async Task Engine_StopsCleanlyOnCancellation()
    {
        var symbol = new Symbol("BTCUSDT");
        var broker = new TestBroker();
        var strategy = new RecordingStrategy(new[] { symbol }, BarInterval.Minute1, 3);
        var context = new RecordingContext(broker);
        var engine = new TradingEngine(broker, strategy, context);
        using var cts = new CancellationTokenSource();

        var run = engine.RunAsync(cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(strategy.Started);
        Assert.True(strategy.Stopped);
        Assert.Contains("disconnect", broker.Events);
        Assert.Contains("dispose", broker.Events);
    }

    [Fact]
    public async Task Engine_PassesLinkedCancellationToStrategy()
    {
        var symbol = new Symbol("BTCUSDT");
        var history = Enumerable.Range(0, 2).Select(i => MakeBar(symbol, i, 100 + i)).ToList();
        var broker = new TestBroker { History = history };
        broker.LiveBars.Writer.TryComplete();
        var strategy = new RecordingStrategy(new[] { symbol }, BarInterval.Minute1, 2);
        var engine = new TradingEngine(broker, strategy, new RecordingContext(broker));
        using var cts = new CancellationTokenSource();

        await engine.RunAsync(cts.Token).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, strategy.Bars.Count);
        Assert.True(strategy.LastToken.CanBeCanceled);
    }

    [Fact]
    public void Constructor_RejectsNullDependencies()
    {
        var broker = new TestBroker();
        var strategy = new RecordingStrategy(new[] { new Symbol("BTCUSDT") }, BarInterval.Minute1, 1);
        var context = new RecordingContext(broker);

        Assert.Throws<ArgumentNullException>(() => new TradingEngine(null!, strategy, context));
        Assert.Throws<ArgumentNullException>(() => new TradingEngine(broker, null!, context));
        Assert.Throws<ArgumentNullException>(() => new TradingEngine(broker, strategy, null!));
    }
}
