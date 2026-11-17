using System.Text.Json;
using TradeCop001.Licensing;

namespace TradeCop001.Tests;

public class SigningTests
{
    [Fact]
    public void GenerateKeys_ProducesDistinctKeyPair()
    {
        var (priv, pub) = Signing.GenerateKeys();

        Assert.False(string.IsNullOrWhiteSpace(priv));
        Assert.False(string.IsNullOrWhiteSpace(pub));
        Assert.NotEqual(priv, pub);
    }

    [Fact]
    public void SignThenVerify_RoundTripsPayload()
    {
        var (priv, pub) = Signing.GenerateKeys();
        var license = new License { Licensee = "round-trip", LicenseId = "ABC123" };

        var envelope = Signing.Sign(license, priv);
        var result = Signing.Verify<License>(envelope, pub);

        Assert.Equal("round-trip", result.Licensee);
        Assert.Equal("ABC123", result.LicenseId);
    }

    [Fact]
    public void Verify_RejectsTamperedPayload()
    {
        var (priv, pub) = Signing.GenerateKeys();
        var envelope = Signing.Sign(new License { Licensee = "original" }, priv);

        var modified = new License { Licensee = "attacker" };
        var originalBytes = Convert.FromBase64String(envelope.Payload);
        var attackerBytes = JsonSerializer.SerializeToUtf8Bytes(modified);
        var tampered = new SignedEnvelope
        {
            Algorithm = envelope.Algorithm,
            Payload = Convert.ToBase64String(attackerBytes),
            Signature = envelope.Signature
        };

        Assert.Throws<InvalidOperationException>(() => Signing.Verify<License>(tampered, pub));
        Assert.NotNull(originalBytes); // guard against unused-variable warning
    }

    [Fact]
    public void Verify_RejectsWrongPublicKey()
    {
        var (priv, _) = Signing.GenerateKeys();
        var (_, otherPub) = Signing.GenerateKeys();
        var envelope = Signing.Sign(new License { Licensee = "x" }, priv);

        Assert.Throws<InvalidOperationException>(() => Signing.Verify<License>(envelope, otherPub));
    }

    [Fact]
    public void Verify_RejectsUnsupportedAlgorithm()
    {
        var (priv, pub) = Signing.GenerateKeys();
        var envelope = Signing.Sign(new License(), priv);
        envelope.Algorithm = "HS256";

        Assert.Throws<InvalidOperationException>(() => Signing.Verify<License>(envelope, pub));
    }

    [Fact]
    public void HashToken_IsDeterministicAndSha256()
    {
        var hash = Signing.HashToken("a-long-random-secret");

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, hash.ToLowerInvariant());
        Assert.Equal(hash, Signing.HashToken("a-long-random-secret"));
        Assert.NotEqual(hash, Signing.HashToken("different"));
    }
}
