# Bridge setup (IB paper / MT5 demo)

The app's `ib` and `mt5` broker adapters do not talk to brokers directly. They
connect to a small native-side JSON-RPC bridge over loopback TCP. Set up the
bridge first, then point the app at it.

Protocol reference: `src/Brokers/TradingBrokers.Bridge/BridgeProtocol.md`.

## 1. IB paper bridge (port 5051)

### Prerequisites

- .NET 10 SDK/runtime.
- TWS or IB Gateway logged in to a **paper** account.
  - TWS paper listens on `127.0.0.1:7496`.
  - IB Gateway paper listens on `127.0.0.1:4001`.

### Build and run

```powershell
cd <repo>\TradingPlatform
dotnet build tools/IbBridge/IbBridge.csproj -c Release
dotnet run --project tools/IbBridge/IbBridge.csproj -c Release
```

On startup the bridge connects to TWS/Gateway, waits for `managedAccounts` and
selects a `DU...` paper account automatically. If you want to pin an account or
change ports, edit `tools/IbBridge/appsettings.json` or pass CLI overrides:

```powershell
dotnet run --project tools/IbBridge/IbBridge.csproj -c Release -- \
  --bridge-port 5051 --ib-port 7496 --client-id 17 --account DU1234567
```

Available overrides: `--bridge-port`, `--ib-port`, `--client-id`, `--account`,
`--allow-live`, `--config <path>`. Host addresses are forced to `127.0.0.1`.

**Paper-only:** order requests are refused unless the selected account id starts
with `DU` (or you explicitly set `AllowLiveTrading=true` / `--allow-live`).
Market data is requested as delayed (type 3), so no paid data subscription is
required. See `tools/IbBridge/README.md` for the `IBApi` NuGet details and the
local-`CSharpAPI.dll` fallback.

## 2. MT5 demo bridge (port 5052)

### Prerequisites

- Windows MetaTrader 5 with a **demo** account logged in.
- Algo Trading enabled and DLL imports allowed for the EA.

### Install

1. Copy `tools/Mt5Bridge/Mt5Bridge.mq5` to `MQL5/Experts/` in your MT5 data
   folder.
2. Compile it in MetaEditor (F7).
3. Attach it to a chart, enable **Allow DLL imports**, keep
   `InpAllowNonDemo=false`, and confirm the Experts log shows
   `Mt5Bridge: listening on 127.0.0.1:5052`.

The EA uses Winsock DLL imports because MQL5's native sockets are outbound-only;
it binds exclusively to `127.0.0.1`. See `tools/Mt5Bridge/README.md` for full
details and the demo-only guard behaviour.

## 3. Verify the listener

With the bridge running, from PowerShell on the same machine:

```powershell
# IB bridge
netstat -ano | findstr :5051
Test-NetConnection 127.0.0.1 -Port 5051

# MT5 bridge
netstat -ano | findstr :5052
Test-NetConnection 127.0.0.1 -Port 5052
```

`TcpTestSucceeded : True` and a `LISTENING` line bound to `127.0.0.1` mean the
bridge is reachable. Do not continue until this succeeds.

## 4. Point the app at the bridge

Edit `src/App/tradecop001/appsettings.json`. The `Broker:Profile` key selects the
broker type, MT5 server and canonical-to-venue symbol map in one step.

### IB paper

```json
"Broker": {
  "Name": "ib-paper",
  "Type": "paper",
  "Profile": "ib-paper",
  "Settings": {
    "BridgeHost": "127.0.0.1",
    "BridgePort": "5051"
  }
}
```

### Deriv MT5 demo

```json
"Broker": {
  "Name": "deriv-demo",
  "Type": "paper",
  "Profile": "deriv-demo",
  "Settings": {
    "BridgeHost": "127.0.0.1",
    "BridgePort": "5052",
    "Mt5Server": "Deriv-Demo"
  }
}
```

### Exness MT5 demo

```json
"Broker": {
  "Name": "exness-demo",
  "Type": "paper",
  "Profile": "exness-demo",
  "Settings": {
    "BridgeHost": "127.0.0.1",
    "BridgePort": "5052"
  }
}
```

`Type` is shown as `paper` because the profile overrides it (`ib`/`mt5`) at
startup. Explicit settings you write are never overwritten by the profile.
`BridgeHost` defaults to `127.0.0.1` and `BridgePort` defaults to the adapter's
own default (5051 for IB, 5052 for MT5), so the values are shown only to make
the configuration explicit. Keep host as `127.0.0.1` — the bridges refuse
non-loopback binds and the strategies assume local-only traffic.

The strategy keeps trading canonical symbols such as `BTCUSDT`; the app maps
them to the venue name (`BTC-USD` on IB, `BTCUSD` on Deriv/Exness) on the wire
and maps bars/ticks back. Verify the mapping in the startup log:
`Symbol mapping active (N venue symbols).`

After changing `appsettings.json`, restart the app. It will connect to the
bridge on startup and the bridge log should show an adapter connection.

## Troubleshooting

- **App reports "Bridge is not connected" / connection refused.** The bridge is
  not running or the port does not match. Re-run the `netstat` check above.
- **IB bridge exits on startup.** Confirm TWS/Gateway is logged in on the
  configured paper port and that `ClientId` is not already in use by another
  API client.
- **IB "no DU account" error.** The logged-in IB account is live. Use a paper
  login, or (only if you intend to trade live) set `AllowLiveTrading=true`.
- **MT5 port not listening.** The EA is not attached, DLL imports were blocked,
  or Algo Trading is off. Check the MT5 Experts/Journal tab.
- **MT5 order rejected as demo-only.** Set `InpAllowNonDemo=true` only when you
  understand and accept that the account is not a demo account.
