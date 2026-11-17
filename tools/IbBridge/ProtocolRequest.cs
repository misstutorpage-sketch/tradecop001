using System.Globalization;
using System.Text.Json;

namespace IbBridge;

/// <summary>A parsed JSON-RPC request with case-insensitive parameter access.</summary>
public sealed class ProtocolRequest
{
    private readonly JsonElement _parameters;

    private ProtocolRequest(int id, string op, JsonElement parameters)
    {
        Id = id;
        Op = op;
        _parameters = parameters;
    }

    public int Id { get; }

    public string Op { get; }

    public static ProtocolRequest? TryParse(string line, out string? error)
    {
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            if (!TryGet(root, "id", out var idElement) || !idElement.TryGetInt32(out var id))
            {
                error = "Request is missing a numeric 'id'.";
                return null;
            }

            if (!TryGet(root, "op", out var opElement) || opElement.ValueKind != JsonValueKind.String)
            {
                error = "Request is missing a string 'op'.";
                return null;
            }

            var parameters = TryGet(root, "params", out var p) && p.ValueKind == JsonValueKind.Object
                ? p.Clone()
                : default;

            return new ProtocolRequest(id, opElement.GetString() ?? "", parameters);
        }
        catch (JsonException ex)
        {
            error = $"Invalid JSON: {ex.Message}";
            return null;
        }
    }

    public string GetString(string name, string fallback = "")
    {
        if (!TryGet(_parameters, name, out var value))
            return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Number => value.GetRawText(),
            _ => fallback
        };
    }

    public decimal GetDecimal(string name, decimal fallback = 0m)
    {
        if (!TryGet(_parameters, name, out var value))
            return fallback;

        if (value.ValueKind == JsonValueKind.String &&
            decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number
            : fallback;
    }

    public int GetInt(string name, int fallback = 0)
    {
        if (!TryGet(_parameters, name, out var value))
            return fallback;

        if (value.ValueKind == JsonValueKind.String &&
            int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : fallback;
    }

    public bool GetBool(string name, bool fallback = false)
    {
        if (!TryGet(_parameters, name, out var value))
            return fallback;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out var parsed) ? parsed : fallback,
            _ => fallback
        };
    }

    public T GetEnum<T>(string name, T fallback) where T : struct, Enum
        => Enum.TryParse(GetString(name), ignoreCase: true, out T result) ? result : fallback;

    private static bool TryGet(JsonElement obj, string name, out JsonElement value)
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
}
