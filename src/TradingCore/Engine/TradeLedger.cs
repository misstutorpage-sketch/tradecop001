using System.Text.Json;
using TradingCore.Models;

namespace TradingCore.Engine;

public sealed record LearningStats(
    int Trades,
    int Wins,
    int Losses,
    decimal WinRatePercent,
    decimal AverageWinPercent,
    decimal AverageLossPercent,
    decimal? ProfitFactor,
    decimal ExpectancyPercent)
{
    public static LearningStats From(IReadOnlyCollection<TradeRecord> trades)
    {
        if (trades.Count == 0)
            return new LearningStats(0, 0, 0, 0m, 0m, 0m, null, 0m);

        var wins = trades.Where(t => t.PnlPercent > 0m).ToArray();
        var losses = trades.Where(t => t.PnlPercent < 0m).ToArray();
        var grossWin = wins.Sum(t => t.PnlPercent);
        var grossLoss = Math.Abs(losses.Sum(t => t.PnlPercent));

        return new LearningStats(
            trades.Count,
            wins.Length,
            losses.Length,
            trades.Count == 0 ? 0m : wins.Length * 100m / trades.Count,
            wins.Length == 0 ? 0m : grossWin / wins.Length,
            losses.Length == 0 ? 0m : -grossLoss / losses.Length,
            grossLoss == 0m ? null : grossWin / grossLoss,
            trades.Sum(t => t.PnlPercent) / trades.Count);
    }
}

public sealed record LearningGroup(string Key, LearningStats Stats);

public sealed record LearningRecommendation(string Scope, string Severity, string Message);

public sealed record LearningSummary(
    bool Enabled,
    string LedgerPath,
    int StoredTrades,
    LearningStats Overall,
    IReadOnlyList<LearningGroup> BySymbol,
    IReadOnlyList<LearningGroup> ByMode,
    IReadOnlyList<LearningGroup> ByRegime,
    IReadOnlyList<LearningRecommendation> Recommendations);

/// <summary>
/// Local, append-only trade ledger. Each closed trade keeps entry/exit prices,
/// the signal and regime that produced it, and 50 context bars on each side.
/// It is plain JSONL on disk and never leaves the machine.
/// </summary>
public sealed class TradeLedger
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly object _sync = new();
    private readonly List<TradeRecord> _records = new();
    private readonly string _path;

    public TradeLedger(string path, bool enabled = true)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A ledger path is required.", nameof(path));

        _path = path;
        Enabled = enabled;
        if (Enabled)
            Load();
    }

    public bool Enabled { get; }
    public string Path => _path;

    public int Count
    {
        get { lock (_sync) return _records.Count; }
    }

    public void Append(TradeRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (_sync)
        {
            _records.Add(record);
            if (!Enabled)
                return;

            var directory = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.AppendAllText(_path, JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);
        }
    }

    public IReadOnlyList<TradeRecord> Recent(int count)
    {
        if (count < 1)
            return Array.Empty<TradeRecord>();

        lock (_sync)
            return _records.TakeLast(count).ToArray();
    }

    public LearningSummary Summarize()
    {
        TradeRecord[] records;
        lock (_sync)
            records = _records.ToArray();

        var overall = LearningStats.From(records);
        var bySymbol = Group(records, r => r.Symbol);
        var byMode = Group(records, r => r.EntryMode);
        var byRegime = Group(records, r => r.Regime);

        return new LearningSummary(
            Enabled,
            _path,
            records.Length,
            overall,
            bySymbol,
            byMode,
            byRegime,
            Recommend(overall, byMode, byRegime));
    }

    private void Load()
    {
        if (!File.Exists(_path))
            return;

        foreach (var line in File.ReadLines(_path))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var record = JsonSerializer.Deserialize<TradeRecord>(line, JsonOptions);
                if (record is not null)
                    _records.Add(record);
            }
            catch (JsonException)
            {
                // A truncated line must not stop the app or lose the rest.
            }
        }
    }

    private static IReadOnlyList<LearningGroup> Group(
        IEnumerable<TradeRecord> records, Func<TradeRecord, string> key)
        => records
            .GroupBy(key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new LearningGroup(g.Key, LearningStats.From(g.ToArray())))
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<LearningRecommendation> Recommend(
        LearningStats overall,
        IReadOnlyList<LearningGroup> byMode,
        IReadOnlyList<LearningGroup> byRegime)
    {
        var list = new List<LearningRecommendation>();

        if (overall.Trades == 0)
        {
            list.Add(new LearningRecommendation(
                "sample", "info", "No closed trades recorded yet. Run demo sessions first."));
            return list;
        }

        if (overall.Trades < 20)
        {
            list.Add(new LearningRecommendation(
                "sample", "info",
                $"Only {overall.Trades} closed trade(s). Treat these numbers as provisional; 20+ are needed for a useful read."));
        }

        if (overall.ProfitFactor is decimal pf && overall.Trades >= 10 && pf < 1m)
        {
            list.Add(new LearningRecommendation(
                "overall", "warning",
                $"Profit factor is {pf:F2} over {overall.Trades} trades. Keep size small or switch to Defensive until expectancy improves."));
        }

        foreach (var group in byMode.Where(g => g.Stats.Trades >= 5 && g.Stats.ExpectancyPercent < 0m))
        {
            list.Add(new LearningRecommendation(
                $"mode:{group.Key}", "warning",
                $"Mode {group.Key} has negative expectancy ({group.Stats.ExpectancyPercent:F2}% over {group.Stats.Trades} trades). Consider Defensive or a higher MinSignalStrength."));
        }

        foreach (var group in byRegime.Where(g => g.Stats.Trades >= 5 && g.Stats.ExpectancyPercent < 0m))
        {
            list.Add(new LearningRecommendation(
                $"regime:{group.Key}", "warning",
                $"Regime {group.Key} has negative expectancy ({group.Stats.ExpectancyPercent:F2}% over {group.Stats.Trades} trades). A regime filter may be needed."));
        }

        if (overall.Losses == 0 && overall.Trades >= 10)
        {
            list.Add(new LearningRecommendation(
                "sample", "warning",
                "No losing trades in this sample. Do not increase size based on an untested winning streak."));
        }

        list.Add(new LearningRecommendation(
            "method", "info",
            "Stats use engine fill prices and percentage returns. Spreads, commission, swaps and contract size can change real account P&L."));

        return list;
    }
}
