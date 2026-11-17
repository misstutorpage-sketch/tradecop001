# Daily updates and progress sharing

`GET /daily` builds a local, read-only summary of the current session. Nothing
is uploaded; the report exists only in the HTTP response.

## Get the report

The app binds to loopback by default and needs the policy control token:

```powershell
$h = @{ Authorization = 'Bearer <your-control-token>' }

# Full JSON (report + adaptive status)
Invoke-RestMethod http://127.0.0.1:5080/daily -Headers $h | ConvertTo-Json -Depth 8

# Share-ready markdown block
(Invoke-RestMethod http://127.0.0.1:5080/daily -Headers $h).report.summary |
  Set-Content daily-update.md
```

The report contains:

- date, state, session start, last error
- broker profile, active broker, policy and licence ids
- strategy name and live adaptive status
- account id, equity and cash when the venue is connected
- open positions with entry, market and unrealised P&L
- every order in this session with side, quantity, status and average fill
- a `summary` markdown block you can paste into your daily update

## Posting template

```markdown
### Day N — <date>

- Venue/account type: HFM <Premium/Pro/Zero/Cent> demo
- Instruments watched: EURUSD, XAUUSD, BTCUSD
- Mode/risk: Balanced / Conservative
- Report: <paste .report.summary here, after removing the account id>
- What I changed: <mode, risk, symbol, size>
- What I observed: <regime, news events, spread>
- Next step: <keep demo / adjust / stay paused>

<one or two sentences in your own words>
```

## What not to share

Before posting publicly, remove or redact:

- account number (`accountId` in the report)
- exact equity/cash if you prefer privacy
- `license.json`, `policy.json`, the control token and the private signing key
- screenshots showing server login details or email addresses

The markdown summary intentionally contains no token, signature, key material or
filesystem paths.

## Important limits

- **Session-only journal.** Order history starts when the app starts and is
  cleared on restart. Export the markdown before restarting if you want a record.
- **No guaranteed profit.** The engine tries to cut losses with stops, targets,
  trailing stops and a high-impact news blackout, but results depend on markets.
- **Demo first.** Keep `InpAllowNonDemo=false` in the MT5 EA and use `hfm-demo`
  until the whole loop — data, orders, exits, daily report — is stable.
- **One symbol per run.** The strategy subscribes to `Strategy:Adaptive:Symbol`.
  Change it between runs or use separate profiles/instances for forex, gold and
  BTC; do not assume simultaneous multi-symbol trading.
