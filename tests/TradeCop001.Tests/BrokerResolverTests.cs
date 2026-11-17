using TradingBrokers.Crypto;
using TradingBrokers.IB;
using TradingBrokers.MT5;
using TradingBrokers.Paper;
using TradingCore.Abstractions;
using TradingCore.Configuration;

namespace TradeCop001.Tests;

public class BrokerResolverTests
{
    private static BrokerResolver MakeResolver() => new(new IBrokerAdapterFactory[]
    {
        new PaperBrokerFactory(),
        new CryptoBrokerFactory(),
        new IBBrokerFactory(),
        new MT5BrokerFactory()
    });

    [Fact]
    public void AvailableTypes_ContainsAllBrokers()
    {
        var types = MakeResolver().AvailableTypes;

        Assert.Contains("paper", types);
        Assert.Contains("crypto", types);
        Assert.Contains("ib", types);
        Assert.Contains("mt5", types);
    }

    [Theory]
    [InlineData("paper", "Paper Simulator")]
    [InlineData("crypto", "Crypto REST")]
    public void Resolve_CreatesCorrectAdapter(string type, string expectedName)
    {
        var broker = MakeResolver().Resolve(new BrokerConfig { Type = type });

        Assert.Equal(expectedName, broker.Name);
    }

    [Fact]
    public void Resolve_RejectsUnknownType()
        => Assert.Throws<NotSupportedException>(() =>
            MakeResolver().Resolve(new BrokerConfig { Type = "does-not-exist" }));
}
