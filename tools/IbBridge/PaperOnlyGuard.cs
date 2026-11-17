namespace IbBridge;

/// <summary>
/// Paper-only safety guard. IB paper account ids start with "DU". A live
/// account id is rejected unless the operator explicitly opts in with
/// AllowLiveTrading / --allow-live.
/// </summary>
public static class PaperOnlyGuard
{
    public static void ValidateConfiguredAccount(string? accountId, bool allowLive)
    {
        if (string.IsNullOrWhiteSpace(accountId) || allowLive || IsPaperAccount(accountId))
            return;

        throw new InvalidOperationException(
            $"PAPER-ONLY guard: account '{accountId}' is not an IB paper (DU...) account. " +
            "Set AccountId to a paper account, leave it empty to auto-select a paper account, " +
            "or set AllowLiveTrading=true / pass --allow-live for an explicit live override.");
    }

    public static string SelectAccount(string? configured, string managedAccountsCsv, bool allowLive)
    {
        ValidateConfiguredAccount(configured, allowLive);

        var accounts = managedAccountsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (!accounts.Any(a => string.Equals(a, configured, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"Configured IB account '{configured}' was not returned by TWS/Gateway (accounts: {string.Join(", ", accounts)}).");

            return configured;
        }

        var paper = accounts.FirstOrDefault(IsPaperAccount);
        if (paper is not null)
            return paper;

        if (allowLive && accounts.Length > 0)
            return accounts[0];

        throw new InvalidOperationException(
            "TWS/Gateway returned no paper (DU...) account. " +
            "Log in to a paper account or pass --allow-live for an explicit live override.");
    }

    public static void EnsureTradingAllowed(string accountId, bool allowLive)
    {
        if (!allowLive && !IsPaperAccount(accountId))
            throw new InvalidOperationException(
                $"PAPER-ONLY guard: refusing to trade non-paper account '{accountId}'.");
    }

    private static bool IsPaperAccount(string accountId)
        => accountId.StartsWith("DU", StringComparison.OrdinalIgnoreCase);
}
