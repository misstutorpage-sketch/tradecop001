using System.Text.Json;
using TradeCop001.Licensing;

if (args.Length == 0)
{
    PrintUsage();
    return;
}

try
{
    switch (args[0].ToLowerInvariant())
    {
        case "keygen":
        {
            var (privateKey, publicKey) = Signing.GenerateKeys();
            File.WriteAllText("tradecop001-private-key.txt", privateKey);
            Console.WriteLine("Private key written to tradecop001-private-key.txt — KEEP THIS OFFLINE.");
            Console.WriteLine("Paste this PUBLIC key into LicensingPublicKey.cs before building:");
            Console.WriteLine(publicKey);
            if (args.Contains("--policy-key"))
            {
                File.WriteAllText("tradecop001-public-key.txt", publicKey);
                Console.WriteLine("Public key also written to tradecop001-public-key.txt");
            }
            break;
        }

        case "fingerprint":
            Console.WriteLine(DeviceFingerprint.Compute());
            break;

        case "issue":
        {
            var license = new License
            {
                Licensee = Option(args, "--licensee", "Unnamed"),
                Notes = Option(args, "--notes", ""),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(int.TryParse(Option(args, "--days", "365"), out var d) ? d : 365),
                MaxDevices = int.TryParse(Option(args, "--max-devices", "1"), out var max) ? max : 1,
                DeviceFingerprints = Option(args, "--devices", "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList(),
                Features = Option(args, "--features", LicenseFeatures.Paper)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList()
            };

            if (license.DeviceFingerprints.Count == 0)
                throw new InvalidOperationException("At least one --devices fingerprint is required.");

            var privateKey = File.ReadAllText(Option(args, "--key", "tradecop001-private-key.txt"));
            var envelope = Signing.Sign(license, privateKey.Trim());
            var output = Option(args, "--out", "license.json");
            File.WriteAllText(output, JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Signed license written to {output} ({license.LicenseId})");
            break;
        }

        case "issue-policy":
        {
            var policy = new OperatingPolicy
            {
                PolicyId = Option(args, "--id", Guid.NewGuid().ToString("N")[..8].ToUpperInvariant()),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(int.TryParse(Option(args, "--days", "365"), out var d) ? d : 365),
                AllowedBrokerTypes = Option(args, "--brokers", "paper")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                AllowedSymbols = Option(args, "--symbols", "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                MaxOrderQuantity = decimal.TryParse(Option(args, "--max-qty", "1"), out var q) ? q : 1m,
                MaxDailyLoss = decimal.TryParse(Option(args, "--max-loss", "1000"), out var l) ? l : 1_000m,
                TradingEnabled = !args.Contains("--disabled"),
                AllowRemoteControl = args.Contains("--allow-remote")
            };

            var token = Option(args, "--control-token", "");
            if (!string.IsNullOrEmpty(token))
                policy.ControlTokenHash = Signing.HashToken(token);

            var start = Option(args, "--start-utc", "");
            var end = Option(args, "--end-utc", "");
            if (TimeOnly.TryParse(start, out var s) && TimeOnly.TryParse(end, out var e))
            {
                policy.StartUtc = s;
                policy.EndUtc = e;
            }

            var privateKey = File.ReadAllText(Option(args, "--key", "tradecop001-private-key.txt"));
            var envelope = Signing.Sign(policy, privateKey.Trim());
            var output = Option(args, "--out", "policy.json");
            File.WriteAllText(output, JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"Signed policy written to {output} ({policy.PolicyId})");
            Console.WriteLine($"Control token hash: {policy.ControlTokenHash}");
            break;
        }

        case "verify":
        {
            var file = Option(args, "--file", "license.json");
            var envelope = JsonSerializer.Deserialize<SignedEnvelope>(File.ReadAllText(file))
                           ?? throw new InvalidOperationException("Empty envelope.");
            var publicKey = Option(args, "--public-key", LicensingPublicKey.Base64);
            if (file.Contains("policy", StringComparison.OrdinalIgnoreCase))
            {
                var policy = Signing.Verify<OperatingPolicy>(envelope, publicKey);
                Console.WriteLine($"Valid policy {policy.PolicyId}, expires {policy.ExpiresAt:u}");
            }
            else
            {
                var license = Signing.Verify<License>(envelope, publicKey);
                Console.WriteLine($"Valid license {license.LicenseId} for {license.Licensee}, expires {license.ExpiresAt:u}");
                Console.WriteLine($"Devices: {string.Join(", ", license.DeviceFingerprints)}");
            }
            break;
        }

        case "token-hash":
            Console.WriteLine(Signing.HashToken(Option(args, "--token", "")));
            break;

        default:
            PrintUsage();
            break;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    Environment.ExitCode = 1;
}

static string Option(string[] args, string name, string fallback)
{
    var index = Array.FindIndex(args, a => a.Equals(name, StringComparison.OrdinalIgnoreCase));
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
}

static void PrintUsage()
{
    Console.WriteLine("""
        tradecop001 LicenseTool

          keygen                              Create signing keys (write private key offline)
          fingerprint                         Print this machine's device fingerprint
          issue --licensee "Name" --devices FP1,FP2 --days 365 --features broker.paper,broker.crypto,control.local
          issue-policy --brokers paper,crypto --symbols BTCUSDT --max-qty 0.1 --control-token SECRET
          verify --file license.json [--public-key BASE64]
          token-hash --token SECRET

        Features: broker.paper, broker.crypto, broker.ib, broker.mt5, control.local, control.remote
        """);
}
