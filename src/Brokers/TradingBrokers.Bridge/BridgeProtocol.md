# Bridge protocol (for the IB / MT5 native side)

Transport: TCP on `127.0.0.1`, one JSON object per line (`\n` terminated), UTF-8.

## Request

```json
{ "id": 1, "op": "account", "params": {} }
```

## Response

```json
{ "id": 1, "ok": true, "result": { ... } }
```

or

```json
{ "id": 1, "ok": false, "error": "message" }
```

## Notifications (no `id`)

```json
{ "op": "orderUpdate", "result": { "brokerOrderId": "123", "clientOrderId": "…",
  "status": "Filled", "filledQuantity": 1, "averageFillPrice": 1.2345 } }
```

```json
{ "op": "bar", "result": { "symbol": "EURUSD", "interval": "M1",
  "bar": { "symbol": { "value": "EURUSD", "exchange": null },
           "openTime": "2026-11-17T10:00:00+00:00",
           "open": 1.1, "high": 1.11, "low": 1.09, "close": 1.105, "volume": 10 } } }
```

```json
{ "op": "tick", "result": { "symbol": "EURUSD",
  "tick": { "symbol": { "value": "EURUSD" }, "time": "…", "bid": 1.1, "ask": 1.1002 } } }
```

## Operations the native side must implement

| `op` | `params` | `result` |
|---|---|---|
| `account` | – | `AccountInfo` |
| `positions` | – | array of `Position` |
| `history` | `symbol`, `interval`, `count` | array of `Bar` |
| `placeOrder` | `symbol`, `side`, `type`, `quantity`, `limitPrice?`, `stopPrice?`, `timeInForce`, `clientOrderId?` | `OrderResult` |
| `cancelOrder` | `orderId`, `symbol?` | `OrderResult` |
| `subscribeBars` | `symbol`, `interval` | `{ "subscribed": true }`, then `bar` notifications |
| `subscribeTicks` | `symbol` | `{ "subscribed": true }`, then `tick` notifications |

All enums are passed as their C# names (`Buy`, `Sell`, `Market`, `Limit`, `Filled`, …).
Field matching is case-insensitive.

## MQL5 EA sketch

An EA can implement the listener with `SocketCreate`/`SocketBind`/`SocketListen`,
then in `OnTimer()` accept a client and read lines with `SocketRead`. Call
`CopyRates()` for `history`, `OrderSend()` for `placeOrder`, and push bars from
`OnTick()` by assembling the JSON manually (MQL5 has no built-in JSON serializer,
or use the community `JASON` include). Run the EA on a chart with
`AlgoTrading` enabled and allow `WebRequest`/socket access for `127.0.0.1`.

For IB, the equivalent bridge is a small C# console app referencing the official
`IBApi.dll` from TWS/Gateway, implementing `EWrapper` callbacks and forwarding
them as the notifications above. Keep TWS/Gateway logged in on port 7496 (TWS)
or 4001 (Gateway); the bridge port (default 5051) is separate and is what this
adapter connects to.
