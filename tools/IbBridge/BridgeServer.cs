using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace IbBridge;

/// <summary>
/// Loopback TCP server. One JSON object per line in both directions, with
/// unsolicited bar/tick/orderUpdate notifications pushed to the active client.
/// </summary>
public sealed class BridgeServer
{
    private readonly BridgeOptions _options;
    private readonly IbSession _broker;
    private readonly TcpListener _listener;
    private TcpClient? _activeClient;

    public BridgeServer(BridgeOptions options, IbSession broker)
    {
        _options = options;
        _broker = broker;
        _listener = new TcpListener(IPAddress.Loopback, options.BridgePort);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _listener.Start();
        Console.WriteLine($"[IbBridge] listening on {_options.BridgeHost}:{_options.BridgePort}");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                if (_activeClient is { Connected: true })
                {
                    Console.WriteLine("[IbBridge] rejected a second client; only one adapter at a time.");
                    client.Dispose();
                    continue;
                }

                _activeClient = client;
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            _listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            using var writer = new StreamWriter(stream, new UTF8Encoding(false))
            {
                AutoFlush = false,
                NewLine = "\n"
            };

            var outbound = Channel.CreateUnbounded<string>();
            void OnNotification(string op, object result)
                => outbound.Writer.TryWrite(ProtocolJson.Notification(op, result));

            _broker.Notification += OnNotification;
            var writerTask = PumpOutboundAsync(outbound, writer, ct);

            try
            {
                string? line;
                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var response = await DispatchAsync(line, ct).ConfigureAwait(false);
                    outbound.Writer.TryWrite(response);
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (IOException ex)
            {
                Console.WriteLine($"[IbBridge] client disconnected: {ex.Message}");
            }
            finally
            {
                _broker.Notification -= OnNotification;
                _broker.CancelSubscriptions();
                outbound.Writer.TryComplete();
                try { await writerTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch { /* best effort on a dead socket */ }

                if (ReferenceEquals(_activeClient, client))
                    _activeClient = null;
            }
        }
    }

    private async Task PumpOutboundAsync(Channel<string> outbound, StreamWriter writer, CancellationToken ct)
    {
        try
        {
            await foreach (var line in outbound.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (IOException)
        {
            // client closed
        }
    }

    private async Task<string> DispatchAsync(string line, CancellationToken ct)
    {
        var request = ProtocolRequest.TryParse(line, out var parseError);
        if (request is null)
            return ProtocolJson.Error(0, parseError ?? "Invalid request.");

        try
        {
            var result = request.Op.ToLowerInvariant() switch
            {
                "account" => await _broker.GetAccountAsync(ct).ConfigureAwait(false),
                "positions" => await _broker.GetPositionsAsync(ct).ConfigureAwait(false),
                "history" => await _broker.GetHistoryAsync(
                    request.GetString("symbol"),
                    request.GetString("interval", "1 min"),
                    request.GetInt("count", 100),
                    ct).ConfigureAwait(false),
                "placeorder" => await _broker.PlaceOrderAsync(request, ct).ConfigureAwait(false),
                "cancelorder" => await _broker.CancelOrderAsync(
                    request.GetString("orderId"),
                    request.GetString("clientOrderId", ""),
                    ct).ConfigureAwait(false),
                "subscribebars" => SubscribeBars(request),
                "subscribeticks" => SubscribeTicks(request),
                _ => throw new InvalidOperationException($"Unknown op '{request.Op}'.")
            };

            return ProtocolJson.Success(request.Id, result);
        }
        catch (Exception ex)
        {
            return ProtocolJson.Error(request.Id, ex.Message);
        }
    }

    private object SubscribeBars(ProtocolRequest request)
    {
        _broker.SubscribeBars(request.GetString("symbol"), request.GetString("interval", "1 min"));
        return new { subscribed = true };
    }

    private object SubscribeTicks(ProtocolRequest request)
    {
        _broker.SubscribeTicks(request.GetString("symbol"));
        return new { subscribed = true };
    }
}
