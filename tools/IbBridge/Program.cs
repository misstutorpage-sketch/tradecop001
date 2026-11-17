using IbBridge;

BridgeOptions options;
try
{
    options = BridgeOptions.Load(args);
    PaperOnlyGuard.ValidateConfiguredAccount(options.AccountId, options.AllowLiveTrading);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[IbBridge] configuration error: {ex.Message}");
    return 1;
}

using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdown.Cancel();
};

var broker = new IbSession(options);
try
{
    await broker.ConnectAsync(shutdown.Token).ConfigureAwait(false);
    Console.WriteLine($"[IbBridge] connected to {options.TwsHost}:{options.TwsPort} as account {broker.Account}.");
    Console.WriteLine(options.AllowLiveTrading
        ? "[IbBridge] WARNING: live-trading override is enabled."
        : "[IbBridge] PAPER-ONLY guard is active.");

    await new BridgeServer(options, broker).RunAsync(shutdown.Token).ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    // Ctrl+C
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[IbBridge] {ex.Message}");
    return 1;
}
finally
{
    await broker.DisconnectAsync().ConfigureAwait(false);
}

return 0;
