# HFM demo day checklist

Goal: watch the complete loop on an HFM **demo** account for a full session —
data, signals, order acceptance, exits and the daily report — before changing
anything.

## Before starting

1. HFM MT5 terminal installed and logged in to a **demo** account.
   - Common demo servers: `HFMarketsGlobal-Demo`, `HFMarketsGlobal-Demo3`,
     `HFMarketsGlobal-Demo4`. Use the name shown in your login dialog.
2. The demo account is the **Premium** type (no symbol suffix), or you have set
   `Broker:Settings:SymbolSuffix` / `SymbolMap` for your type.
3. `tools\Mt5Bridge\Mt5Bridge.mq5` compiled in MetaEditor and attached to a
   chart with `InpAllowNonDemo=false`.
4. Experts log shows `Mt5Bridge: listening on 127.0.0.1:5052`.
5. Market Watch shows the instruments you plan to test, e.g. `#BTCUSD`,
   `XAUUSD`, `EURUSD`. Symbols not visible in Market Watch cannot be traded.
6. Choose one canonical symbol per run: `BTCUSDT`, `XAUUSD`, `EURUSD`,
   `GBPUSD`, `USDJPY` or `AUDUSD`.

Do not set `InpAllowNonDemo=true` during this phase.

## Start

```powershell
cd <repo>
powershell -NoProfile -ExecutionPolicy Bypass -File _licensetest\start-hfm-demo.ps1 -Symbol XAUUSD
```

The script refuses to start unless the MT5 bridge is listening. It also prints
the local control token, policy id and limits.

Safe defaults for this demo policy:

- broker type: `mt5` only
- symbols: `BTCUSDT`, `EURUSD`, `GBPUSD`, `USDJPY`, `AUDUSD`, `XAUUSD`
- max order: `0.05` lots
- daily loss stop: `100` USD
- trading enabled, no remote control

## During the session

Watch the console for:

- `replayed N historical bars` and `Warmup complete — adaptive signals enabled.`
- `Signal ... -> OpenLong/OpenShort/Close`
- `Order not accepted (...)` or `Order blocked: ...`
- `Engine error: ...` or market-data errors

Check status at any time:

```powershell
$token = Get-Content -Raw _licensetest\hfm-demo-token.txt
$h = @{ Authorization = "Bearer $token" }
Invoke-RestMethod http://127.0.0.1:5080/status -Headers $h
Invoke-RestMethod http://127.0.0.1:5080/adaptive -Headers $h
```

Useful controls:

```powershell
# Pause new orders immediately
Invoke-RestMethod http://127.0.0.1:5080/pause -Method Post -Headers $h
Invoke-RestMethod http://127.0.0.1:5080/resume -Method Post -Headers $h

# Switch behaviour without restarting
Invoke-RestMethod http://127.0.0.1:5080/mode -Method Post -Headers $h -ContentType application/json -Body '{"mode":"defensive"}'
Invoke-RestMethod http://127.0.0.1:5080/risk -Method Post -Headers $h -ContentType application/json -Body '{"risk":"conservative"}'
```

## Save the daily update

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File _licensetest\get-hfm-daily.ps1
```

It writes `_licensetest\daily-updates\hfm-demo-YYYY-MM-DD.md` and prints the
markdown. Remove the account id before posting publicly.

## What to record for tomorrow's review

| Question | Why it matters |
|---|---|
| Did the bridge stay connected for the whole session? | Disconnects invalidate the test |
| Were all orders accepted at the expected size? | Size below venue minimum or wrong symbol map |
| Any rejected orders? Exact message? | Policy, symbol suffix, market closed or margin |
| Did stop loss / take profit / trailing exit fire as expected? | Risk control is the main purpose |
| Spread at entry and exit | Scalping settings may be too tight for HFM spreads |
| Number of trades and win/loss after costs | One day is observation, not proof |
| Did the daily report match the MT5 terminal history? | Confirms journal/mapping correctness |

## Stop conditions

Pause or stop the session if any of these happen:

- orders rejected repeatedly for the same reason
- a position is opened without a working stop
- quantity is larger than expected
- the bridge disconnects and does not recover
- daily loss approaches the `100` USD policy stop
- a symbol maps to a different instrument than Market Watch shows

## Before any live upgrade

A single demo day is not enough evidence. Require at least several stable demo
sessions, no unexplained rejections, correct exits and a reviewed daily report.
Then reissue a **separate live policy** with a tiny maximum size and a small
daily loss limit, and keep `hfm-demo` as the default profile.
