using System.Globalization;
using System.Text.Json;

namespace IbBridge;

/// <summary>Loopback-only configuration, loaded from appsettings.json with CLI overrides.</summary>
public sealed record BridgeOptions(
    string BridgeHost,
    int BridgePort,
    string TwsHost,
    int TwsPort,
    int ClientId,
    string AccountId,
    bool AllowLiveTrading,
    string ConfigPath)
{
    public static BridgeOptions Load(string[] args)
    {
        var configPath = GetValue(args, "config", "appsettings.json") ?? "appsettings.json";

        var bridgeHost = "127.0.0.1";
        var bridgePort = 5051;
        var twsHost = "127.0.0.1";
        var twsPort = 7496;
        var clientId = 17;
        var accountId = "";
        var allowLive = false;

        if (File.Exists(configPath))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = doc.RootElement;

            if (TrySection(root, "Bridge", out var bridge))
            {
                bridgeHost = ReadString(bridge, "Host", bridgeHost);
                bridgePort = ReadInt(bridge, "Port", bridgePort);
            }

            if (TrySection(root, "Ib", out var ib))
            {
                twsHost = ReadString(ib, "Host", twsHost);
                twsPort = ReadInt(ib, "Port", twsPort);
                clientId = ReadInt(ib, "ClientId", clientId);
                accountId = ReadString(ib, "AccountId", accountId);
                allowLive = ReadBool(ib, "AllowLiveTrading", allowLive);
            }

            // Flat keys are accepted as well, matching the app's Broker settings style.
            bridgeHost = ReadString(root, "BridgeHost", bridgeHost);
            bridgePort = ReadInt(root, "BridgePort", bridgePort);
            twsHost = ReadString(root, "TwsHost", twsHost);
            twsPort = ReadInt(root, "TwsPort", twsPort);
            clientId = ReadInt(root, "ClientId", clientId);
            accountId = ReadString(root, "AccountId", accountId);
        }

        bridgeHost = GetValue(args, "bridge-host", bridgeHost)!;
        twsHost = GetValue(args, "ib-host", twsHost)!;
        bridgePort = int.TryParse(GetValue(args, "bridge-port", bridgePort.ToString(CultureInfo.InvariantCulture)),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var bp) ? bp : bridgePort;
        twsPort = int.TryParse(GetValue(args, "ib-port", twsPort.ToString(CultureInfo.InvariantCulture)),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var tp) ? tp : twsPort;
        clientId = int.TryParse(GetValue(args, "client-id", clientId.ToString(CultureInfo.InvariantCulture)),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var ci) ? ci : clientId;
        accountId = GetValue(args, "account", accountId)!;
        allowLive |= HasFlag(args, "allow-live");

        return new BridgeOptions(
            BridgeHost: NormalizeLoopback(bridgeHost, "Bridge:Host"),
            BridgePort: ValidatePort(bridgePort, "Bridge:Port"),
            TwsHost: NormalizeLoopback(twsHost, "Ib:Host"),
            TwsPort: ValidatePort(twsPort, "Ib:Port"),
            ClientId: clientId,
            AccountId: accountId.Trim(),
            AllowLiveTrading: allowLive,
            ConfigPath: configPath);
    }

    private static string NormalizeLoopback(string host, string setting)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(host, "127.0.0.1", StringComparison.Ordinal))
            return "127.0.0.1";

        throw new InvalidOperationException(
            $"{setting} must be 127.0.0.1 (or localhost); this bridge is loopback-only. Got '{host}'.");
    }

    private static int ValidatePort(int port, string setting)
        => port is >= 1 and <= 65535
            ? port
            : throw new InvalidOperationException($"{setting} must be between 1 and 65535.");

    private static bool TrySection(JsonElement root, string name, out JsonElement section)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out section))
            return true;

        section = default;
        return false;
    }

    private static string ReadString(JsonElement obj, string name, string fallback)
        => TryProperty(obj, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static int ReadInt(JsonElement obj, string name, int fallback)
        => TryProperty(obj, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result)
            ? result
            : fallback;

    private static bool ReadBool(JsonElement obj, string name, bool fallback)
        => TryProperty(obj, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static bool TryProperty(JsonElement obj, string name, out JsonElement value)
    {
        if (obj.ValueKind == JsonValueKind.Object)
        {
            if (obj.TryGetProperty(name, out value))
                return true;

            foreach (var property in obj.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string? GetValue(string[] args, string name, string fallback)
    {
        var prefix = $"--{name}";
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith(prefix + "=", StringComparison.Ordinal))
                return args[i][(prefix.Length + 1)..];

            if (string.Equals(args[i], prefix, StringComparison.Ordinal) && i + 1 < args.Length)
                return args[i + 1];
        }

        return fallback;
    }

    private static bool HasFlag(string[] args, string name)
        => args.Any(a => string.Equals(a, $"--{name}", StringComparison.OrdinalIgnoreCase));
}
