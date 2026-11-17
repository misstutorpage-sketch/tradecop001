using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TradeCop001.Licensing;

/// <summary>Signed wrapper used for both licenses and operating policies.</summary>
public sealed class SignedEnvelope
{
    public string Algorithm { get; set; } = "ES256";
    public string Payload { get; set; } = "";
    public string Signature { get; set; } = "";
}

/// <summary>ECDSA P-256 signing/verification. Private key stays with you, never shipped.</summary>
public static class Signing
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static (string PrivateKey, string PublicKey) GenerateKeys()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (
            Convert.ToBase64String(key.ExportPkcs8PrivateKey()),
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    }

    public static SignedEnvelope Sign<T>(T payload, string privateKeyBase64)
    {
        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyBase64), out _);

        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
        var signature = key.SignData(payloadBytes, HashAlgorithmName.SHA256);

        return new SignedEnvelope
        {
            Algorithm = "ES256",
            Payload = Convert.ToBase64String(payloadBytes),
            Signature = Convert.ToBase64String(signature)
        };
    }

    public static T Verify<T>(SignedEnvelope envelope, string publicKeyBase64)
    {
        if (!string.Equals(envelope.Algorithm, "ES256", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsupported signature algorithm.");

        using var key = ECDsa.Create();
        key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);

        var payloadBytes = Convert.FromBase64String(envelope.Payload);
        var signature = Convert.FromBase64String(envelope.Signature);

        if (!key.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256))
            throw new InvalidOperationException("Signature verification failed — file was altered or signed by a different key.");

        return JsonSerializer.Deserialize<T>(payloadBytes, Json)
               ?? throw new InvalidOperationException("Signed payload is empty.");
    }

    public static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
