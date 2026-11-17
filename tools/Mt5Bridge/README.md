# Mt5Bridge

Native-side JSON-RPC bridge for the tradecop001 MT5 adapter. It implements the
protocol in `src/Brokers/TradingBrokers.Bridge/BridgeProtocol.md` and listens on
`127.0.0.1:5052` only. It is an MQL5 Expert Advisor, not a separate process.

## Files

- `Mt5Bridge.mq5` — the EA. It opens a loopback TCP listener with Winsock,
  accepts one adapter connection at a time, and translates JSON-RPC requests
  into MQL5 calls (`CopyRates`, `OrderSend`, account/position queries).

## Why Winsock DLL imports instead of MQL5 sockets

MQL5's built-in socket API (`SocketCreate`, `SocketConnect`, `SocketSend`,
`SocketRead`) is **outbound-only**: there is no `SocketBind`/`SocketListen`/
`SocketAccept`. To host the listener required by this protocol, the EA imports
the Windows Winsock functions from `ws2_32.dll` directly. Consequences:

- **Windows MetaTrader 5 only** (Winsock is Windows-specific).
- You must enable **Allow DLL imports** for the EA when attaching it to a chart.
- The bind address is hardcoded to `127.0.0.1` (`LOOPBACK_ADDRESS`). There is no
  configuration option to bind to a non-loopback interface.
- The terminal must be allowed to use network sockets for `127.0.0.1` (allow
  `WebRequest`/socket access in the terminal options if prompted).

## Install and run

1. Copy `Mt5Bridge.mq5` into your MT5 data folder:
   `MQL5/Experts/Mt5Bridge.mq5`.
2. Open **MetaEditor** and compile it (F7). Fix any include-path issues; only
   standard MQL5 headers are used.
3. In MT5, open a chart for a symbol you will trade (the EA does not require
   any specific chart symbol; it uses the symbol from each request).
4. Attach the EA to the chart. In the dialog:
   - Enable **Allow Algo Trading** / **Allow DLL imports**.
   - **InpPort** = `5052` (default).
   - **InpTimerMs** = `100` (socket poll interval; default is fine).
   - **InpMagic** = `773311` (magic number written to orders the bridge sends).
   - **InpAllowNonDemo** = `false` (keep the demo-only guard enabled).
5. Turn on the **Algo Trading** toolbar button (AutoTrading) so `OnTradeTransaction`
   can fire for `orderUpdate` notifications.
6. Check the **Experts/Journal** tab for
   `Mt5Bridge: listening on 127.0.0.1:5052`.

The EA supports both 32-bit and 64-bit terminals (the Winsock `SOCKET` type is
selected at startup via `TERMINAL_X64`).

## Demo-only safety guard

- By default the EA accepts order requests only when the account trade mode is
  `ACCOUNT_TRADE_MODE_DEMO`. Read-only ops (`account`, `positions`, `history`,
  subscriptions) still work on any account.
- Setting `InpAllowNonDemo=true` explicitly removes that restriction. Leave it
  `false` when connecting to a demo account.
- There is no autostart, no service registration, no credential collection and
  no hidden persistence. All state is in memory and is cleared on disconnect.

## Protocol coverage

Requests: `account`, `positions`, `history`, `placeOrder`, `cancelOrder`,
`subscribeBars`, `subscribeTicks`.

Notifications:

- `bar` — pushed from `OnTick()`/`OnTimer()` when a new bar opens for a
  subscribed symbol+interval.
- `tick` — pushed from `OnTick()` for subscribed symbols using current BID/ASK.
- `orderUpdate` — pushed from `OnTradeTransaction()` when MT5 reports an order,
  deal, request or position change.

Interval strings follow the MT5 adapter mapping (`M1`, `M5`, `M15`, `H1`, `D1`);
the IB-style strings (`1 min`, `5 mins`, …) are also accepted for convenience.
`timeInForce: GTC` maps to `ORDER_TIME_GTC`, otherwise `ORDER_TIME_DAY`. Pending
order types (`Limit`, `Stop`, `StopLimit`) use `TRADE_ACTION_PENDING`; market
orders use `TRADE_ACTION_DEAL`. Filling mode is derived from the symbol's
`SYMBOL_FILLING_MODE`.

## Verify the listener

From Windows PowerShell on the same machine:

```powershell
netstat -ano | findstr :5052
Test-NetConnection 127.0.0.1 -Port 5052
```

Both should show a listener on `127.0.0.1:5052` while the EA is attached. The
`netstat` output includes the owning PID, which should be `terminal.exe`.

## Limitations

- One adapter connection is handled at a time; a reconnect replaces the old
  session.
- `cancelOrder` expects the numeric MT5 order/deal/position ticket in
  `orderId`. Pending orders are removed with `TRADE_ACTION_REMOVE`; an order id
  that matches an open position is closed with an opposite deal.
- There is no local build/compile verification in this repository: MetaEditor is
  required. The code was reviewed for MQL5 syntax but has not been compiled here.
