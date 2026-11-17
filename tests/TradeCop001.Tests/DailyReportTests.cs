using TradeCop001;
using TradingCore.Engine;
using TradingCore.Models;

namespace TradeCop001.Tests;

public class DailyReportTests
{
    private static JournalEntry Fill(string symbol, OrderSide side, decimal qty, decimal price)
        => new(DateTimeOffset.Now, symbol, side, OrderType.Market, qty,
            OrderStatus.Filled, qty, price, "B-1", "test");

    [Fact]
    public void Build_IncludesAccountPositionsAndOrderTable()
    {
        var report = DailyReportBuilder.Build(
            DateTimeOffset.Now,
            "hfm-demo",
            "MetaTrader 5 [mapped] [policy enforced] [journal]",
            "adaptive morphic",
            "Running",
            DateTimeOffset.UtcNow.AddHours(-2),
            null,
            "POL-1",
            "LIC-1",
            "None (offline)",
            false,
            new AccountInfo("12345678", 10_500m, 10_000m, 20_000m, "USD"),
            new[]
            {
                new Position(new Symbol("XAUUSD"), 0.01m, 2400m, 2410m)
            },
            new[]
            {
                Fill("BTCUSDT", OrderSide.Buy, 0.01m, 65000m),
                Fill("XAUUSD", OrderSide.Buy, 0.01m, 2400m)
            });

        Assert.Equal("hfm-demo", report.BrokerProfile);
        Assert.Equal(2, report.FilledOrders);
        Assert.Equal(0.02m, report.FilledVolume);
        Assert.Equal(0, report.RejectedOrders);
        Assert.Contains("## tradecop001 daily update", report.Summary);
        Assert.Contains("Equity 10,500.00 USD", report.Summary);
        Assert.Contains("| XAUUSD | 0.01 | 2400 | 2410 | 0.1 |", report.Summary);
        Assert.Contains("| BTCUSDT | Buy |", report.Summary);
        Assert.Contains("Session-only journal", report.Summary);
    }

    [Fact]
    public void Build_HandlesOfflineAccountAndNoOrders()
    {
        var report = DailyReportBuilder.Build(
            DateTimeOffset.Now,
            "paper-primary",
            null,
            "adaptive morphic",
            "Stopped",
            null,
            "engine offline",
            "POL-1",
            "LIC-1",
            "None (offline)",
            false,
            null,
            Array.Empty<Position>(),
            Array.Empty<JournalEntry>());

        Assert.Null(report.Equity);
        Assert.Equal(0, report.FilledOrders);
        Assert.Contains("not connected yet", report.Summary);
        Assert.Contains("Last error: engine offline", report.Summary);
        Assert.Contains("_Flat_", report.Summary);
        Assert.Contains("_No orders in this session yet._", report.Summary);
    }
}
