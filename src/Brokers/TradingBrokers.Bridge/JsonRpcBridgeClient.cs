using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TradingBrokers.Bridge;

/// <summary>
/// Minimal newline-delimited JSON-RPC client. A small local process (an IBApi
/// console app or an MQL5 EA) speaks the other end. See BridgeProtocol.md.
/// </summary>
public sealed class JsonRpcBridgeClient : IAsyncDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();

    private TcpClient? _tcp;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _lifetime;
    private Task? _readLoop;
    private int _nextId;

    public event Action<string, JsonElement>? Notification;

    public bool IsConnected => _tcp?.Connected == true;

    public JsonRpcBridgeClient(string host, int port)
    {
        _host = host;
        _port = port;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(_host, _port, ct).ConfigureAwait(false);

        var stream = _tcp.GetStream();
        _reader = new StreamReader(stream, Encoding.UTF8);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };

        _lifetime = new CancellationTokenSource();
        _readLoop = Task.Run(() => ReadLoopAsync(_lifetime.Token), _lifetime.Token);
    }

    public async Task<JsonElement> CallAsync(string op, object? parameters, CancellationToken ct = default)
    {
        if (_writer is null || !IsConnected)
            throw new InvalidOperationException("Bridge is not connected.");

        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var payload = JsonSerializer.Serialize(new { id, op, @params = parameters ?? new { } });

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(payload).ConfigureAwait(false);
            await _writer.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            return await tcs.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        _lifetime?.Cancel();
        if (_readLoop is not null)
        {
            try { await _readLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* ignore */ }
        }
        _lifetime?.Dispose();
        _lifetime = null;
        _readLoop = null;

        _writer?.Dispose();
        _reader?.Dispose();
        _tcp?.Dispose();
        _writer = null;
        _reader = null;
        _tcp = null;

        foreach (var pending in _pending.Values)
            pending.TrySetCanceled();
        _pending.Clear();
    }

    public async ValueTask DisposeAsync() => await DisconnectAsync().ConfigureAwait(false);

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (_reader is not null && !ct.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                    break;
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                if (!root.TryGetProperty("id", out var idElement))
                {
                    // Unsolicited push message.
                    var op = root.TryGetProperty("op", out var opElement) ? opElement.GetString() ?? "" : "";
                    var result = root.TryGetProperty("result", out var r) ? r.Clone() : default;
                    Notification?.Invoke(op, result);
                    continue;
                }

                var id = idElement.GetInt32();
                if (!_pending.TryGetValue(id, out var tcs))
                    continue;

                var ok = !root.TryGetProperty("ok", out var okElement) || okElement.GetBoolean();
                if (ok)
                {
                    var result = root.TryGetProperty("result", out var r) ? r.Clone() : default;
                    tcs.TrySetResult(result);
                }
                else
                {
                    var message = root.TryGetProperty("error", out var e) ? e.GetString() : "Bridge error";
                    tcs.TrySetException(new InvalidOperationException(message));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected
        }
        catch (Exception ex)
        {
            foreach (var pending in _pending.Values)
                pending.TrySetException(ex);
        }
    }
}
