# tradecop001 licensing and access control

The app refuses to start unless it has a valid signed license for the current device.
There are no hidden mechanisms: the license and policy files are plain signed JSON
that the operator can inspect. Nothing is installed silently, no autostart entry is
created, and nothing is sent anywhere.

## 1. Install the SDK

- Direct Windows x64 installer (.NET 10 LTS):
  https://dotnet.microsoft.com/en-us/download/dotnet/thank-you/sdk-10.0.404-windows-x64-installer
- All versions: https://dotnet.microsoft.com/en-us/download/dotnet/10.0
- Verify: `dotnet --version`

## 2. Generate your signing keys (do this once, on your own machine)

```powershell
dotnet run --project src/App/tradecop001.LicenseTool -- keygen
```

This writes `tradecop001-private-key.txt` in the current directory and prints the
public key. **Keep the private key offline and never ship it.** Paste the public key
into `src/TradeCop001.Licensing/LicensingPublicKey.cs`:

```csharp
public const string Base64 = "<paste public key here>";
```

## 3. Collect device fingerprints

On each device you want to allow, run the app once:

```powershell
tradecop001.exe --fingerprint
```

You can also get it with `LicenseTool fingerprint` when running from source.

## 4. Issue a license

```powershell
dotnet run --project src/App/tradecop001.LicenseTool -- issue `
  --licensee "Device A" `
  --devices a1b2c3d4e5f60718,9988776655443322 `
  --max-devices 2 `
  --days 365 `
  --features broker.paper,broker.crypto,broker.ib,broker.mt5,control.local `
  --out license.json
```

Copy `license.json` next to `tradecop001.exe` on each approved device. Alternatively
set `TRADECOP_LICENSE` to the JSON contents. The app checks:

- ECDSA signature (so the file cannot be edited),
- expiry date,
- the device fingerprint is in the allowed list,
- `MaxDevices` is not exceeded,
- the selected broker type is covered by a `broker.*` feature.

## 5. Issue an operating policy (optional but recommended)

```powershell
dotnet run --project src/App/tradecop001.LicenseTool -- issue-policy `
  --brokers paper,crypto `
  --symbols BTCUSDT,EURUSD `
  --max-qty 0.05 `
  --max-loss 250 `
  --start-utc 08:00 --end-utc 20:00 `
  --control-token "a-long-random-secret" `
  --out policy.json
```

Copy `policy.json` next to the exe. The policy is enforced locally by
`PolicyEnforcingBroker`, which wraps the selected adapter and rejects any order that
violates symbol, quantity, schedule, or loss limits. It also holds the hash of the
control token.

`--allow-remote` additionally requires the `control.remote` license feature. Only use
it if you intend to bind the control API to a non-loopback interface.

## 6. Local development without a license

For development only:

```powershell
$env:Licensing__RequireLicense = 'false'
dotnet run --project src/App/tradecop001
```

Never ship an `appsettings.json` with this override in it.

## Control API

Enabled when the policy contains a control token hash. Binds to `127.0.0.1:5080` by
default. Every request needs `Authorization: Bearer <token>`.

| Endpoint | Effect |
|---|---|
| `GET /status` | state, device, license, policy, options, pause state |
| `POST /start` | start the engine |
| `POST /stop` | stop the engine |
| `POST /pause` | block new orders without stopping data feed |
| `POST /resume` | allow orders again |
| `POST /config` | update strategy options (used on next start) |

Example:

```powershell
curl -H "Authorization: Bearer a-long-random-secret" http://127.0.0.1:5080/status
```

Reaching a loopback-bound API from another machine requires the operator's own VPN
or remote-desktop setup. That is deliberate: the app does not open an inbound path
to the internet by itself.

## What is intentionally not included

No hidden persistence, no process hiding, no anti-analysis, no unattended remote
desktop, no keylogging, no credential harvesting, and no silent autostart. Access on
third-party machines depends on their informed consent and your own OS-level remote
access arrangement.
