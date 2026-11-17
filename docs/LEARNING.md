# Local trade learning

`tradecop001` records each completed round-trip **locally** so the morphic engine
can be reviewed and improved from your own fills. This is deliberately a
data-collection and reporting feature, not an auto-trading or auto-tuning one.

## What is recorded

When the adaptive strategy closes a position after a confirmed fill, it emits a
`TradeCompleted` event. A local subscriber appends one JSON line to
`trades.jsonl` (beside the executable by default) containing:

- symbol, direction, entry/exit time, entry/exit price and quantity
- realised return as a percentage, and the exit reason (stop loss, take profit,
  trailing stop, opposite signal, …)
- the mode and risk profile at entry **and** at exit
- the detected market regime and the signal strength/reason at entry
- the last 50 market bars before entry and the last 50 bars at exit (OHLCV)

The context bars are what let you see *what the market looked like* when a
decision worked or failed. A small sample is not proof of anything; use the data
to form hypotheses, then test changes on demo before any live use.

## Privacy boundary — read this

- The ledger is a plain local JSONL file. Nothing is uploaded, and the app makes
  no AI, MCP or telemetry network calls for learning.
- MT5's **AI Assistant** and any **MCP endpoints** are separate third-party
  features. They may analyse market/account data, charts, positions, history,
  source code, logs and tester results, call MCP tools, access the web, and send
  that material to selected providers under their own terms and privacy
  policies. `tradecop001` cannot read those systems and does not participate in
  them. Only data you deliberately export from your terminal can be reviewed
  here.
- Never put passwords, API keys or payment details into prompts or exports.
- AI-generated output can be inaccurate, incomplete or unsafe. Review and verify
  all of it. This is not financial, investment, trading, legal, tax or
  cybersecurity advice, and no maximum profit is guaranteed. The practical aim
  is loss minimisation.

## Configuration

In `appsettings.json`:

```json
"Learning": {
  "Enabled": true,
  "LedgerPath": "trades.jsonl"
}
```

- `Enabled: false` (or `--Learning:Enabled=false`) disables disk writes. Trades
  are still emitted in memory for the session, but nothing is persisted.
- `LedgerPath` may be absolute or relative to the executable directory.

Learning failures are swallowed and logged (`Learning record failed: …`) so a
disk problem can never stop trading. A truncated/corrupt JSON line is skipped on
load without losing the rest of the ledger.

## Viewing the data

The control endpoint is token-protected and read-only:

```powershell
$h = @{ Authorization = 'Bearer <control-token>' }
Invoke-RestMethod http://127.0.0.1:5080/learning -Headers $h
```

Response shape:

- `summary` — enabled flag, ledger path, trade count, overall `LearningStats`
  (wins, losses, win rate, average win/loss, profit factor, expectancy), and
  breakdowns `BySymbol`, `ByMode`, `ByRegime`
- `recent` — the 10 newest trades without context bars (counts are included)

Recommendations are advisory text flags such as:

- fewer than 20 trades → numbers are provisional
- profit factor below 1 over 10+ trades → consider Defensive / smaller size
- a mode or regime with negative expectancy over 5+ trades → consider a filter
- an untested winning streak → do not increase size on it alone

No recommendation changes engine parameters. Any tuning is a deliberate,
reviewed decision you make and then re-test on demo.

## What this does *not* do

- No auto-tuning of signals, stops, regime weights or size.
- No upload of trades, bars, logs, code or account data.
- No reading of MT5 AI Assistant, external MCP servers or web resources.
- No guarantee of profitability; one demo day is observation, not evidence.
- No cross-session order journal — the daily `/daily` report is still
  session-only; only closed trades are persisted here.

## Reviewing a demo day

Use the checklist in `docs/HFM_DEMO_CHECKLIST.md` and the share template in
`docs/DAILY_UPDATES.md`. Combine the `/daily` report with `/learning` stats,
compare against MT5's own history, and look at the stored context bars around
the trades that lost the most. Keep size tiny and only consider a live policy
after several clean demo sessions.
