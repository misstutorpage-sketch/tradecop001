using System.Globalization;
using System.Text;
using TradingCore.Engine;
using TradingCore.Models;

namespace TradeCop001;

/// <summary>One day's trading summary, built locally and safe to share.</summary>
public sealed record DailyReport(
    string Date,
    DateTimeOffset GeneratedAt,
    string BrokerProfile,
    string? BrokerName,
    string Strategy,
    string State,
    DateTimeOffset? StartedAt,
    string? LastError,
    string PolicyId,
    string LicenseId,
    string ResearchProvider,
    bool NewsBlackout,
    string? AccountId,
    decimal? Equity,
    decimal? Cash,
    string? Currency,
    IReadOnlyList<Position> Positions,
    IReadOnlyList<JournalEntry> Orders,
    int FilledOrders,
    int RejectedOrders,
    decimal FilledVolume,
    string Summary);

public static class DailyReportBuilder
{
    public static DailyReport Build(
        DateTimeOffset now,
        string brokerProfile,
        string? brokerName,
        string strategy,
        string state,
        DateTimeOffset? startedAt,
        string? lastError,
        string policyId,
        string licenseId,
        string researchProvider,
        bool newsBlackout,
        AccountInfo? account,
        IReadOnlyList<Position> positions,
        IReadOnlyList<JournalEntry> orders)
    {
        var ordered = orders.OrderBy(o => o.Timestamp).ToArray();
        var filled = ordered.Where(o => o.IsFill && o.FilledQuantity > 0m).ToArray();
        var rejected = ordered.Count(o => o.Status is OrderStatus.Rejected or OrderStatus.Cancelled);
        var filledVolume = filled.Sum(o => o.FilledQuantity);

        var report = new DailyReport(
            now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            now,
            brokerProfile,
            brokerName,
            strategy,
            state,
            startedAt,
            lastError,
            policyId,
            licenseId,
            researchProvider,
            newsBlackout,
            account?.AccountId,
            account?.Equity,
            account?.Cash,
            account?.Currency,
            positions,
            ordered,
            filled.Length,
            rejected,
            filledVolume,
            string.Empty);

        return report with { Summary = Render(report) };
    }

    private static string Render(DailyReport report)
    {
        var text = new StringBuilder();
        text.AppendLine($"## tradecop001 daily update — {report.Date}");
        text.AppendLine();
        text.AppendLine($"- State: {report.State}" +
            (report.StartedAt is null ? "" : $" since {report.StartedAt:yyyy-MM-dd HH:mm} UTC"));
        text.AppendLine($"- Broker: {report.BrokerProfile}" +
            (string.IsNullOrWhiteSpace(report.BrokerName) ? "" : $" ({report.BrokerName})"));
        text.AppendLine($"- Strategy: {report.Strategy}");
        text.AppendLine($"- Policy: {report.PolicyId} | Licence: {report.LicenseId}");
        text.AppendLine($"- Research: {report.ResearchProvider} (news blackout {(report.NewsBlackout ? "on" : "off")})");

        if (report.Equity is not null)
        {
            text.AppendLine(
                $"- Account: {report.AccountId} | Equity {Money(report.Equity)} {report.Currency}" +
                (report.Cash is null ? "" : $" | Cash {Money(report.Cash)}"));
        }
        else
        {
            text.AppendLine("- Account: not connected yet (venue snapshot unavailable)");
        }

        if (!string.IsNullOrWhiteSpace(report.LastError))
            text.AppendLine($"- Last error: {report.LastError}");

        text.AppendLine(
            $"- Orders today: {report.Orders.Count} ({report.FilledOrders} filled, {report.RejectedOrders} closed/rejected), " +
            $"filled volume {report.FilledVolume.ToString("0.####", CultureInfo.InvariantCulture)}");

        text.AppendLine();
        text.AppendLine("### Open positions");
        if (report.Positions.Count == 0)
        {
            text.AppendLine("_Flat_");
        }
        else
        {
            text.AppendLine("| Symbol | Qty | Entry | Market | Unrealised |");
            text.AppendLine("|---|---:|---:|---:|---:|");
            foreach (var position in report.Positions)
            {
                text.AppendLine(
                    $"| {position.Symbol.Value} | {position.Quantity.ToString("0.####", CultureInfo.InvariantCulture)} " +
                    $"| {Number(position.AverageEntryPrice)} | {Number(position.MarketPrice)} " +
                    $"| {Number(position.UnrealizedPnl)} |");
            }
        }

        text.AppendLine();
        text.AppendLine("### Orders today");
        if (report.Orders.Count == 0)
        {
            text.AppendLine("_No orders in this session yet._");
        }
        else
        {
            text.AppendLine("| Time | Symbol | Side | Qty | Status | Avg fill |");
            text.AppendLine("|---|---|---|---:|---|---:|");
            foreach (var order in report.Orders)
            {
                text.AppendLine(
                    $"| {order.Timestamp:HH:mm:ss} | {order.Symbol} | {order.Side} " +
                    $"| {order.Quantity.ToString("0.####", CultureInfo.InvariantCulture)} | {order.Status} " +
                    $"| {(order.IsFill ? Number(order.AverageFillPrice) : "-")} |");
            }
        }

        text.AppendLine();
        text.AppendLine("_Session-only journal: restarting the app clears order history. " +
                        "Account values are read from the venue at request time._");
        return text.ToString().TrimEnd();
    }

    private static string Money(decimal? value)
        => value?.ToString("N2", CultureInfo.InvariantCulture) ?? "-";

    private static string Number(decimal? value)
        => value?.ToString("0.#####", CultureInfo.InvariantCulture) ?? "-";
}
