# tradecop001

A broker-agnostic, device-licensed trading tool built on .NET 10.

Your strategy depends only on `IBrokerAdapter` and `IStrategyContext`. The concrete
broker is selected at startup by configuration; a signed license limits which devices
may run the app, and a signed policy controls what it is allowed to do.

## Layout

```
tradecop001.sln
├─ src/TradingCore/               models, interfaces, engine, broker resolver
├─ src/Brokers/
│  ├─ TradingBrokers.Paper/       working simulator (no credentials)
│  ├─ TradingBrokers.Crypto/      Binance-style REST + WebSocket
│  ├─ TradingBrokers.Bridge/      shared JSON-RPC bridge for native APIs
│  ├─ TradingBrokers.IB/          Interactive Brokers adapter
│  └─ TradingBrokers.MT5/         MetaTrader 5 adapter
├─ src/TradeCop001.Licensing/     device fingerprint, signed license, policy enforcement
├─ src/Strategies/                your algorithms implement IStrategy
└─ src/App/
   ├─ tradecop001/                main app + localhost control API
   └─ tradecop001.LicenseTool/    key generation, licensing, policy issuance
```

## SDK

- Direct installer (Windows x64, .NET 10 LTS):
  https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/sdk-10.0.404-windows-x64-installer
- All downloads: https://dotnet.microsoft.com/en-us/download/dotnet/10.0
- Check: `dotnet --version`

## Build and run locally

```powershell
cd TradingPlatform
dotnet build tradecop001.sln -c Release

# Local dev without a license (never ship this override):
$env:Licensing__RequireLicense = 'false'
dotnet run --project src/App/tradecop001
```

Press Ctrl+C to stop.

## License and device restriction

See `docs/LICENSING.md` for the full workflow. Short version:

```powershell
dotnet run --project src/App/tradecop001.LicenseTool -- keygen
# paste the printed public key into src/TradeCop001.Licensing/LicensingPublicKey.cs

tradecop001.exe --fingerprint                       # on each target device
dotnet run --project src/App/tradecop001.LicenseTool -- issue --licensee "Device A" --devices <fingerprint> --features broker.paper,control.local
dotnet run --project src/App/tradecop001.LicenseTool -- issue-policy --brokers paper --control-token SECRET
```

Place the generated `license.json` and `policy.json` beside the exe. The app refuses
to start if the signature, device, expiry, or feature checks fail.

The control API binds to `127.0.0.1:5080` and requires `Authorization: Bearer <token>`.
It supports `/status`, `/start`, `/stop`, `/pause`, `/resume`, and `/config`.

## Publish a shareable app

After setting your public key:

```powershell
.\publish.ps1
```

Output goes to `publish\win-x64` — a self-contained single-file `tradecop001.exe`
plus the recipient README. Ship that folder plus a license for each device.
Never include `tradecop001-private-key.txt`.

## Write your own strategy

Implement `IStrategy`:

```csharp
public sealed class MyStrategy : IStrategy
{
    public string Name => "My strategy";
    IReadOnlyList<Symbol> IStrategy.Subscriptions => new[] { new Symbol("BTCUSDT") };
    public BarInterval Interval => BarInterval.Minute1;
    public int RequiredHistoryBars => 100;
    public void OnStarted(IStrategyContext ctx) { }
    public void OnWarmupComplete(IStrategyContext ctx) { }
    public void OnBar(Bar bar, IStrategyContext ctx) { /* ctx.Broker is the selected broker */ }
    public void OnStopped(IStrategyContext ctx) { }
}
```

Register it in `Program.cs`. The engine replays history before enabling live signals.

## Broker notes

- Crypto speaks the Binance-style API directly (ccxt has no official .NET build).
- IB needs a local bridge using the official `IBApi.dll`.
- MT5 needs an MQL5 EA bridge; see `src/Brokers/TradingBrokers.Bridge/BridgeProtocol.md`.

## Scope

This is consent-based licensing and local policy enforcement. There is no hidden
persistence, process hiding, remote desktop, credential collection, or internet
phone-home. Access to a machine remains its owner's decision and OS-level remote
access arrangement.
