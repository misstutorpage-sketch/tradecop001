namespace TradeCop001.Licensing;

/// <summary>
/// IMPORTANT: replace this with the public key printed by the LicenseTool
/// `keygen` command BEFORE you build the distributable. The private key must
/// never be placed in the shipped app.
/// </summary>
public static class LicensingPublicKey
{
    public const string Base64 = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEkYT93vowifSgNJBut1WI482H/resNzvhP53Mu2fMN2Wbn7yj8rWAIuvBaI6mDQHmEns6xhNAjObjo2XNT+xT2A==";

    public static bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Base64) &&
        Base64 != "REPLACE_WITH_YOUR_PUBLIC_KEY_BASE64";
}
