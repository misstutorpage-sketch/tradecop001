using System.Threading.Channels;
using TradingCore.Abstractions;
using TradingCore.Models;

namespace TradingCore.Engine;

/// <summary>
/// Wires one strategy to one broker adapter. Swap either one and nothing else changes.
/// Historical bars are replayed first (so indicators are warmed), then live bars stream in.
/// </summary>
public sealed class TradingEngine
{
    private readonly IBrokerAdapter _broker;
    private readonly IStrategy _strategy;
    private readonly IStrategyContext _context;

    public TradingEngine(IBrokerAdapter broker, IStrategy strategy, IStrategyContext context)
    {
        _broker = broker ?? throw new ArgumentNullException(nameof(broker));
        _strategy = strategy ?? throw new ArgumentNullException(nameof(strategy));
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        await _broker.ConnectAsync(ct).ConfigureAwait(false);
        _context.Log($"Connected to broker adapter: {_broker.Name}");
        _strategy.OnStarted(_context);

        var channel = Channel.CreateBounded<Bar>(new BoundedChannelOptions(1_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pumps = _strategy.Subscriptions
            .Select(symbol => PumpAsync(symbol, channel.Writer, linkedCts.Token))
            .ToArray();

        try
        {
            await foreach (var bar in channel.Reader.ReadAllAsync(linkedCts.Token).ConfigureAwait(false))
            {
                _strategy.OnBar(bar, _context);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown (Ctrl+C).
        }
        finally
        {
            linkedCts.Cancel();
            channel.Writer.TryComplete();
            _strategy.OnStopped(_context);

            try
            {
                await Task.WhenAll(pumps).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch
            {
                // Don't block shutdown on a stuck market-data stream.
            }

            await _broker.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
            await _broker.DisposeAsync().ConfigureAwait(false);
            _context.Log("Engine stopped cleanly.");
        }
    }

    private async Task PumpAsync(Symbol symbol, ChannelWriter<Bar> writer, CancellationToken ct)
    {
        try
        {
            var history = await _broker.GetHistoricalBarsAsync(
                symbol,
                _strategy.Interval,
                Math.Max(1, _strategy.RequiredHistoryBars),
                ct).ConfigureAwait(false);

            foreach (var bar in history.OrderBy(b => b.OpenTime))
            {
                await writer.WriteAsync(bar, ct).ConfigureAwait(false);
            }

            _context.Log($"[{symbol}] replayed {history.Count} historical bars.");
            _strategy.OnWarmupComplete(_context);

            await foreach (var liveBar in _broker.StreamBarsAsync(symbol, _strategy.Interval, ct).ConfigureAwait(false))
            {
                await writer.WriteAsync(liveBar, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        catch (Exception ex)
        {
            _context.Log($"[{symbol}] market-data error: {ex.Message}");
        }
    }
}
