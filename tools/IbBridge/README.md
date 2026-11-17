# IbBridge

Local-only JSON-RPC bridge between the tradecop001 IB adapter and a TWS/Gateway
paper session. It implements the protocol in
`src/Brokers/TradingBrokers.Bridge/BridgeProtocol.md` on `127.0.0.1:5051`.

## Build

```powershell
cd <repo>\TradingPlatform
dotnet build tools/IbBridge/IbBridge.csproj -c Release
```

Verified with .NET SDK 10.0.404: **build succeeds with 0 warnings and 0 errors**.

## IBApi dependency

The project uses the official Interactive Brokers C# API from NuGet:

```xml
<PackageReference Include="IBApi" Version="1.0.0-preview-975" />
```

`IBApi` 1.0.0-preview-975 is the only version published on nuget.org. Its
assembly is `CSharpAPI.dll` and its target framework is `netstandard2.0`, which
.NET 10 can consume — the build above confirms this. Note that this package
wraps IB API v9.75; if your TWS/Gateway is much newer and refuses the handshake,
use the local-DLL fallback below with the `IBApi.dll` shipped in your TWS API
installation.

### Local IBApi.dll fallback (optional)

Replace the `PackageReference` with a direct reference, for example:

```xml
<ItemGroup>
  <!-- Disable when using the NuGet package -->
  <!-- <PackageReference Include="IBApi" Version="1.0.0-preview-975" /> -->
  <Reference Include="CSharpAPI">
    <HintPath>C:\TWS API\source\CSharpClient\IBApi\bin\Release\CSharpAPI.dll</HintPath>
    <Private>true</Private>
  </Reference>
</ItemGroup>
```

The DLL name in recent TWS API installs is usually `CSharpAPI.dll` (the older
`IBApi.dll` name is also accepted by the app code once referenced). Adjust the
`HintPath` to your installation and rebuild. Keep either the package or the
reference, not both.

## Run

```powershell
dotnet run --project tools/IbBridge/IbBridge.csproj -c Release
# or
.\tools\IbBridge\bin\Release\net10.0\IbBridge.exe
```

TWS paper must be logged in on `127.0.0.1:7496`, or IB Gateway paper on
`127.0.0.1:4001`. Those are the ports requested by this project's setup;
override with `--ib-port` if your installation uses different local paper ports.

The bridge stops on Ctrl+C. There is no autostart, no service registration, no
credential collection and no persistence beyond `appsettings.json`.

### Configuration

`appsettings.json` (copied to output):

```json
{
  "Bridge": { "Host": "127.0.0.1", "Port": 5051 },
  "Ib": {
    "Host": "127.0.0.1",
    "Port": 7496,
    "ClientId": 17,
    "AccountId": "",
    "AllowLiveTrading": false
  }
}
```

CLI overrides: `--bridge-port`, `--ib-port`, `--client-id`, `--account`,
`--allow-live`, `--config <path>`. Both host values are forced to `127.0.0.1`;
any other bind address is rejected.

## PAPER-ONLY safety guard

- IB paper account ids start with `DU` (e.g. `DU1234567`).
- `AccountId` may be left empty: the bridge waits for `managedAccounts` from
  TWS and selects a `DU...` account automatically.
- A configured live account id, or a TWS session with no `DU...` account, is
  refused at startup and on every order unless `AllowLiveTrading=true` (or
  `--allow-live`) is set explicitly.
- Market data is requested as delayed (type 3) so paper accounts work without
  paid data subscriptions.

## Protocol coverage

Requests: `account`, `positions`, `history`, `placeOrder`, `cancelOrder`,
`subscribeBars`, `subscribeTicks`.
Notifications: `orderUpdate` (from `orderStatus` / IB errors), `bar` (from
`historicalDataUpdate`, i.e. completed live bars at the requested interval),
`tick` (from `tickPrice` BID/ASK, timestamped from `LAST_TIMESTAMP` when IB
provides it).

Bar subscriptions request `keepUpToDate=true` historical data; the initial
backfill is intentionally not forwarded as notifications, only live bar
updates are. Symbol-to-contract mapping is heuristic (6-letter symbols become
forex `CASH` with the last three letters as currency; everything else is
`STK`/`SMART`/`USD`). Optional `exchange` param overrides that for
`placeOrder`/history/subscriptions.

## Verify the listener

```powershell
netstat -ano | findstr :5051
Test-NetConnection 127.0.0.1 -Port 5051
```
