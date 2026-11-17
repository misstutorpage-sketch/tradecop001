using TradingCore.Engine;
using TradingCore.Models;

namespace TradeCop001.Tests;

public class TradeLedgerTests
{
    private static TradeRecord Trade(
        string symbol = "EURUSD",
        decimal pnl = 1m,
        string mode = "Trend",
        string regime = "TrendingUp",
        string exitReason = "take profit",
        int contextBars = 3)
    {
        var t = DateTimeOffset.UtcNow;
        var symbols = new Symbol(symbol);
        var ctx = Enumerable.Range(0, contextBars)
            .Select(i => new Bar(symbols, t.AddMinutes(i), 1m, 1.1m, 0.9m, 1m, 100m))
            .ToArray();
        return new TradeRecord(
            symbol, "Buy", t, t.AddMinutes(10), 1m, 1.01m, 0.01m, pnl, exitReason,
            mode, "Balanced", mode, "Balanced", regime, 0.7m, "Trend:Buy momentum", ctx, ctx);
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"tradcop-ledger-{Guid.NewGuid():N}.jsonl");

    [Fact]
    public void Append_PersistsJsonlAndReloadsOnNewInstance()
    {
        var path = TempPath();
        try
        {
            var ledger = new TradeLedger(path);
            ledger.Append(Trade(pnl: 2m));
            ledger.Append(Trade(pnl: -1m, symbol: "XAUUSD"));

            Assert.Equal(2, ledger.Count);
            Assert.Equal(2, File.ReadAllLines(path).Length);

            var reloaded = new TradeLedger(path);
            Assert.Equal(2, reloaded.Count);
            Assert.Contains(reloaded.Recent(10), t => t.Symbol == "XAUUSD" && t.PnlPercent == -1m);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_SkipsCorruptLineWithoutLosingOtherRecords()
    {
        var path = TempPath();
        try
        {
            File.WriteAllText(path, "{not-json}" + Environment.NewLine);
            var ledger = new TradeLedger(path);
            ledger.Append(Trade(pnl: 1m));

            Assert.Equal(1, ledger.Count);
            Assert.Equal("EURUSD", ledger.Recent(1)[0].Symbol);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void DisabledLedger_DoesNotWriteToDisk()
    {
        var path = TempPath();
        try
        {
            var ledger = new TradeLedger(path, enabled: false);
            ledger.Append(Trade());

            Assert.Equal(1, ledger.Count); // still available in memory this session
            Assert.False(File.Exists(path));
            Assert.False(ledger.Summarize().Enabled);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Summarize_ComputesStatsAndGroups()
    {
        var ledger = new TradeLedger(TempPath(), enabled: false);
        ledger.Append(Trade(symbol: "EURUSD", pnl: 2m, mode: "Trend", regime: "TrendingUp"));
        ledger.Append(Trade(symbol: "EURUSD", pnl: -1m, mode: "Trend", regime: "TrendingUp"));
        ledger.Append(Trade(symbol: "XAUUSD", pnl: 3m, mode: "Range", regime: "Ranging"));

        var summary = ledger.Summarize();

        Assert.Equal(3, summary.StoredTrades);
        Assert.Equal(3, summary.Overall.Trades);
        Assert.Equal(2, summary.Overall.Wins);
        Assert.Equal(1, summary.Overall.Losses);
        Assert.Equal(2m * 100m / 3m, summary.Overall.WinRatePercent);
        Assert.Equal(4m / 3m, summary.Overall.ExpectancyPercent);
        Assert.Equal(5m, summary.Overall.ProfitFactor); // 5 gross win / 1 gross loss
        Assert.Contains(summary.BySymbol, g => g.Key == "EURUSD" && g.Stats.Trades == 2);
        Assert.Contains(summary.BySymbol, g => g.Key == "XAUUSD" && g.Stats.Trades == 1);
        Assert.Contains(summary.ByMode, g => g.Key == "Trend" && g.Stats.Trades == 2);
        Assert.Contains(summary.ByRegime, g => g.Key == "Ranging" && g.Stats.Trades == 1);
    }

    [Fact]
    public void Summarize_EmptyLedgerReportsInfoRecommendation()
    {
        var summary = new TradeLedger(TempPath(), enabled: false).Summarize();

        Assert.Equal(0, summary.StoredTrades);
        Assert.Null(summary.Overall.ProfitFactor);
        Assert.Contains(summary.Recommendations, r => r.Severity == "info" && r.Scope == "sample");
    }

    [Fact]
    public void Recommend_WarnsOnLosingProfitFactorAndRegime()
    {
        var ledger = new TradeLedger(TempPath(), enabled: false);
        for (var i = 0; i < 12; i++)
            ledger.Append(Trade(pnl: i % 2 == 0 ? 0.5m : -1m, mode: "Scalp", regime: "Ranging"));

        var summary = ledger.Summarize();

        Assert.Contains(summary.Recommendations, r => r.Scope == "overall" && r.Severity == "warning");
        Assert.Contains(summary.Recommendations, r => r.Scope == "mode:Scalp" && r.Severity == "warning");
        Assert.Contains(summary.Recommendations, r => r.Scope == "regime:Ranging" && r.Severity == "warning");
        Assert.Contains(summary.Recommendations, r => r.Scope == "method" && r.Severity == "info");
    }

    [Fact]
    public void Recent_ReturnsLastNRecordsInOrder()
    {
        var ledger = new TradeLedger(TempPath(), enabled: false);
        for (var i = 1; i <= 5; i++)
            ledger.Append(Trade(symbol: $"S{i}", pnl: i));

        var recent = ledger.Recent(2);

        Assert.Equal(2, recent.Count);
        Assert.Equal("S4", recent[0].Symbol);
        Assert.Equal("S5", recent[1].Symbol);
        Assert.Empty(ledger.Recent(0));
    }
}
