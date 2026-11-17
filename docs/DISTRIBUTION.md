# tradecop001 — recipient instructions

You have been given a copy of tradecop001, a broker-agnostic trading tool.

## Run it

1. Unzip the folder into a directory you can write to.
2. Confirm `license.json` and `policy.json` are in the same folder as `tradecop001.exe`.
3. Double-click `tradecop001.exe`, or run it from PowerShell.
4. Press Ctrl+C to stop cleanly.

No .NET installation is required: the app is published self-contained.

The app reads the license and policy files on startup. If your machine is not on the
approved device list, or the license has expired, it will print a clear message and
exit. Your device fingerprint is shown in that message; send it to the issuer if you
need access.

## What the app does

- Runs the configured trading strategy against the configured broker adapter.
- Enforces the signed operating policy: allowed brokers/symbols, maximum order size,
  trading window, and daily loss limit.
- Listens on `127.0.0.1:5080` for a token-protected control API (if a token was set).
  It does not listen on the public network and does not phone home.

You can read `policy.json` and `license.json` — they are signed JSON documents, not
encrypted secrets.

## Control API (optional)

From that machine:

```
curl -H "Authorization: Bearer <token>" http://127.0.0.1:5080/status
curl -X POST -H "Authorization: Bearer <token>" http://127.0.0.1:5080/pause
curl -X POST -H "Authorization: Bearer <token>" http://127.0.0.1:5080/resume
```

Ask the issuer for the token. Use `/pause` to stop new orders immediately.

## Safety

Automated trading can lose money. Start with the paper broker and small size. You are
responsible for your own broker credentials and risk controls.
