# tradecop001 — Morphic AI Trading Tool

One .NET 10 executable that runs an adaptive ("morphic") trading engine across
paper, crypto, Interactive Brokers and MetaTrader 5 (Deriv / Exness / HFM). A signed
licence binds it to a device; a signed policy remains the final, hard boundary
on symbol, size, hours and daily loss.

**Honest framing:** no algorithm can guarantee maximum profit. The engine aims
to *minimise losses* by detecting market regimes, re-weighting several signals,
and automatically opening/closing positions with stop-loss, take-profit and
trailing-stop exits. Profitability still depends on the market.

## What is integrated

| Capability | Status |
|---|---|
| Adaptive morphic engine (trend + momentum + breakout, regime-weighted) | Built, unit-tested, running on paper |
| Automatic open/close with stops, targets, trailing stop, cooldown | Built |
| Live adjustments through token-protected localhost API (presets + tuning) | Built |
| Broker profiles: Deriv MT5, Exness MT5, HFM MT5, IB paper, paper, crypto | Built |
| Canonical symbol mapping (one strategy works on every venue) | Built |
| HFM Premium/Pro/Zero/Cent/KATANA symbol-suffix overrides | Built |
| Daily progress report (`GET /daily`, local, share-ready) | Built |
| Local trade learning: closed-trade ledger + stats (`GET /learning`) | Built; no auto-tuning, no uploads |
| Research/news interface + high-impact-event blackout (opt-in, offline default) | Built; no live feed yet |
| Optional external AI signal adapter | Interface built; inert by default |
| IB paper bridge (official `IBApi`, loopback JSON-RPC) | Built, compiles clean |
| MT5 demo EA bridge (Deriv / Exness / HFM MT5) | Source complete; compile in MetaEditor |
| Device licence + signed operating policy | Built, tested |

## Layout

```
tradecop001.sln
├─ src/TradingCore/
│  ├─ Abstractions/            IBrokerAdapter, IStrategy, IResearchProvider
│  ├─ Adapters/                MappedBrokerAdapter (canonical <-> venue symbols)
│  ├─ Configuration/           BrokerResolver, BrokerProfiles
│  ├─ Engine/                  TradingEngine, TradeLedger (local JSONL learning)
│  └─ Models/                  Symbol, Bar, Tick, TradeRecord, OrderRequest/Result
├─ src/Brokers/
│  ├─ TradingBrokers.Paper/    working simulator (no credentials)
│  ├─ TradingBrokers.Crypto/   Binance-style REST + WebSocket
│  ├─ TradingBrokers.Bridge/   shared JSON-RPC bridge client + protocol spec
│  ├─ TradingBrokers.IB/       Interactive Brokers adapter
│  └─ TradingBrokers.MT5/      MetaTrader 5 adapter
├─ src/Strategies/
│  ├─ Strategies.MovingAverage/ SMA crossover (simple reference strategy)
│  └─ Strategies.Adaptive/      morphic AI engine, presets, signals, AI seam
├─ src/TradeCop001.Licensing/  licence, policy, news blackout, order enforcement
├─ src/App/
│  ├─ tradecop001/             main app + control API
│  └─ tradecop001.LicenseTool/ key generation, licensing, policy issuance
├─ tools/IbBridge/             .NET 10 IB paper bridge (127.0.0.1:5051)
├─ tools/Mt5Bridge/            MQL5 EA bridge for Deriv/Exness/HFM MT5 (127.0.0.1:5052)
└─ docs/                       BRIDGE_SETUP.md, DAILY_UPDATES.md, LEARNING.md, LICENSING.md
```

## SDK

- .NET 10 LTS SDK `10.0.404`:
  https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/sdk-10.0.404-windows-x64-installer
- Check with `dotnet --version`.

## Build, test, run

```powershell
cd TradingPlatform
dotnet build tradecop001.sln -c Release
dotnet test tradecop001.sln -c Release --nologo -v minimal   # 117 tests, all green

# Local development without a licence (never ship this override):
$env:Licensing__RequireLicense = 'false'
dotnet run --project src/App/tradecop001
```

Press Ctrl+C to stop. The default configuration is deliberately safe:
**paper broker + adaptive strategy + Conservative risk + no shorting + offline research**.

## Safe progression

1. **Paper simulator** (default) — no account, no money. Learn the controls.
2. **IB paper / MT5 demo** — start the local bridge, select a broker profile.
3. **Binance testnet** — supply testnet keys through broker settings only.
4. **Live, tiny size** — only after demo/paper are stable for weeks.

## One tool, many venues — broker profiles

Set `Broker:Profile` and the app selects the broker type, MT5 server and symbol
map automatically. Explicit settings you write are never overwritten.

| Profile | Broker | Typical use |
|---|---|---|
| `deriv-demo` / `deriv-real` | MT5 | Deriv, including Volatility 25/50/75/100 indices |
| `exness-demo` / `exness-real` | MT5 | Exness MT5 |
| `hfm-demo` / `hfm-real` | MT5 | HFM (HF Markets), Premium/no-suffix symbols |
| `ib-paper` | IB | Interactive Brokers paper via bridge |
| *(empty)* | paper | local simulator (default) |

Example `appsettings.json` for Deriv demo (one change):

```json
"Broker": {
  "Name": "deriv-demo",
  "Type": "paper",
  "Profile": "deriv-demo",
  "Settings": {
    "BridgeHost": "127.0.0.1",
    "BridgePort": "5052"
  }
}
```

The strategy still trades `BTCUSDT`; `MappedBrokerAdapter` translates it to
`BTCUSD` on the wire and translates bars/ticks back. The signed policy is checked
against the **canonical** symbol before mapping, so one policy works everywhere.

### HFM (HF Markets)

`hfm-demo` uses `HFMarketsGlobal-Demo`; `hfm-real` uses
`HFMarketsGlobal-Live1`. Defaults follow the **Premium** account, which has no
symbol suffix. Built-in canonical names cover forex, crypto, metals and indices:

| Canonical | HFM Premium | Notes |
|---|---|---|
| `EURUSD`, `GBPUSD`, `USDJPY`, `AUDUSD`, `USDCAD`, `USDCHF`, `NZDUSD` | same | majors |
| `EURGBP`, `EURJPY`, `GBPJPY` | same | crosses |
| `BTCUSDT` | `#BTCUSD` | crypto uses `#` |
| `ETHUSDT` | `#ETHUSD` | |
| `XAUUSD`, `XAGUSD` | same | gold / silver |
| `US30` | `USA30` | |
| `NAS100` | `USA100` | |
| `SPX500` | `USA500` | |
| `GER40`, `UK100`, `JPN225` | same | |

```json
"Broker": {
  "Name": "hfm-demo",
  "Type": "paper",
  "Profile": "hfm-demo",
  "Settings": {
    "BridgeHost": "127.0.0.1",
    "BridgePort": "5052"
  }
}
```

**Other HFM account types.** HFM appends a suffix per account. Set one suffix,
or override individual symbols (overrides always win):

```json
"Settings": {
  "SymbolSuffix": "r",
  "SymbolMap": "BTCUSDT=#BTCUSDr;XAUUSD=XAUUSDr;EURUSD=EURUSDr"
}
```

Typical suffixes: Premium none, Pro `r`, Zero `b` (crypto often `#BTCUSDr`),
Cent `c`, KATANA/InfinityX `x`, Premium Pro `.V`. If your terminal shows a
different name, copy it from MT5 Market Watch into `SymbolMap` — no code change.
Use `--Broker:Profile=hfm-demo --Broker:Settings:SymbolMap="..."` to try it
without editing the file.

Order size is raised to the venue minimum when Conservative scaling would go
below it: `0.01` lots for forex, gold, silver, BTC and ETH; `0.1` for indices.
Mode/risk switches no longer shrink or grow size repeatedly.

Daily progress reports live in `GET /daily`; see `docs/DAILY_UPDATES.md`.
Local trade learning lives in `GET /learning`; see `docs/LEARNING.md`.
For the first HFM demo session, follow `docs/HFM_DEMO_CHECKLIST.md`.

See `docs/BRIDGE_SETUP.md` for IB paper and MT5 demo bridge setup.

## Adaptive morphic engine

Default under `Strategy:Adaptive`. Presets switch all tuning numbers with one
call while keeping your symbol and size:

- Modes: `Balanced`, `Trend`, `Range`, `Scalp`, `Defensive`
- Risk: `Conservative` (default), `Balanced`, `Aggressive`
- Signals: SMA trend, RSI momentum, range breakout; weighted by detected regime
  (`TrendingUp`, `TrendingDown`, `Ranging`, `Volatile`)
- Exits: stop loss, take profit, trailing stop, opposite-signal exit, cooldown
- Shorting is off unless `EnableShorting` is true; policy still has the final say
- Optional `IAiSignalProvider`: no network call unless `AiEnabled` is true and a
  real provider is injected — the shipped provider is inert

## Control API

Binds to `127.0.0.1:5080` by default and requires `Authorization: Bearer <token>`.
Requests without a valid token get `401`. Non-loopback binds require the signed
policy's `--allow-remote` plus the licence's `control.remote` feature.

```powershell
$h = @{ Authorization = 'Bearer test-token-123' }

Invoke-RestMethod http://127.0.0.1:5080/status -Headers $h
Invoke-RestMethod http://127.0.0.1:5080/adaptive -Headers $h
Invoke-RestMethod http://127.0.0.1:5080/daily -Headers $h
Invoke-RestMethod http://127.0.0.1:5080/learning -Headers $h
Invoke-RestMethod http://127.0.0.1:5080/research -Headers $h

# Save today's share-ready markdown:
(Invoke-RestMethod http://127.0.0.1:5080/daily -Headers $h).report.summary |
  Set-Content daily-update.md

# One-call adjustments while running:
Invoke-RestMethod http://127.0.0.1:5080/mode -Method Post -Headers $h -ContentType application/json -Body '{"mode":"trend"}'
Invoke-RestMethod http://127.0.0.1:5080/risk -Method Post -Headers $h -ContentType application/json -Body '{"risk":"aggressive"}'
Invoke-RestMethod http://127.0.0.1:5080/adaptive/config -Method Post -Headers $h -ContentType application/json -Body '{ ...full AdaptiveOptions... }'

# Safety and engine:
Invoke-RestMethod http://127.0.0.1:5080/pause -Method Post -Headers $h
Invoke-RestMethod http://127.0.0.1:5080/resume -Method Post -Headers $h
Invoke-RestMethod http://127.0.0.1:5080/stop -Method Post -Headers $h
```

`/status` reports state, policy/licence ids, broker profile, active broker,
venue symbol count, research provider, news-blackout status, pause state and SMA
options. `/adaptive` reports mode, risk, regime, signal, open position,
unrealised P&L and last action. `/daily` returns a JSON report plus a
`summary` markdown block for your daily update (session orders, open positions,
account snapshot when the venue is connected). Order history is session-only:
restarting the app clears it.

`/learning` returns a local-only summary of real closed trades (win rate,
profit factor, expectancy, and breakdowns by symbol/mode/regime) plus the 10
most recent trades without their context bars. It never uploads data or changes
parameters automatically — see `docs/LEARNING.md`.

## Local trade learning

Every confirmed round-trip is appended to `trades.jsonl` beside the executable
with its entry/exit prices, signal, regime and the last 50 market bars around
both entry and exit. `GET /learning` summarises these and suggests what to
review; it does **not** auto-tune the engine. All of it stays on this device.
Set `Learning:Enabled=false` (or pass `--Learning:Enabled=false`) to turn the
ledger off, and `Learning:LedgerPath` to relocate it. See `docs/LEARNING.md`.

The MT5 **AI Assistant** and external **MCP endpoints** are separate products:
they may send market, account, history, code and logs to third parties under
their own terms. This tool cannot read that data and never contacts AI
providers or MCP servers. Only data you explicitly export from your terminal
can be reviewed here.

## Research / news

Per the chosen safe option, the interface exists but **no live feed is bundled**.
`INewsProvider` is offline by default (`NullNewsProvider`), so the app makes no
network calls. `Research:EnableBlackout` is `false` by default; when enabled with
a real provider, `NewsBlackoutGuard` blocks new orders for 15 minutes before and
after high-impact events (NFP, CPI, rate decisions). A live RSS/API feed can be
plugged in later without changing the strategy or policy.

## Licence and device restriction

See `docs/LICENSING.md`. Short version:

```powershell
dotnet run --project src/App/tradecop001.LicenseTool -- keygen
# paste the printed public key into src/TradeCop001.Licensing/LicensingPublicKey.cs

tradecop001.exe --fingerprint                       # on each target device
dotnet run --project src/App/tradecop001.LicenseTool -- issue --licensee "Device A" --devices <fingerprint> --features broker.paper,broker.mt5,control.local
dotnet run --project src/App/tradecop001.LicenseTool -- issue-policy --brokers paper,mt5 --control-token SECRET
```

Place the generated `license.json` and `policy.json` beside the exe. The app
refuses to start on signature, device, expiry or feature failure.

## Publish a shareable app

```powershell
.\publish.ps1
```

Output: `publish\win-x64\tradecop001.exe` (self-contained single file) plus a
recipient README. Ship it with a licence for each device. **Never** include
`tradecop001-private-key.txt`, `license.json` or `policy.json` in the repo.

## Scope and privacy

Consent-based licensing and local policy enforcement only. No hidden persistence,
process hiding, remote desktop, credential collection, autostart or internet
phone-home. The bridges bind to loopback only and never collect broker
credentials — those stay in your TWS/MT5 terminal or bridge-local config.

Trade learning writes a plain `trades.jsonl` beside the executable and the
`/learning` view is local-only; there is no upload path. MT5's AI Assistant and
external MCP endpoints are third-party features with their own data terms — this
tool neither uses nor extracts their data, and never sends prompts, code, logs,
market or account data to any AI provider or MCP endpoint. Any AI output is
advisory only: review and verify it yourself. This is not financial, investment,
trading, legal, tax or cybersecurity advice, and no maximum profit is implied.
