using Iris.Core.Lookup;
using Xunit;

namespace Iris.Tests.Lookup;

public class OrgFetchGateTests
{
    private const string Sid = "ORG1";
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly OrgFetchGate _gate = new();

    [Fact]
    public void RepeatedFailuresPauseAskingWithoutEndingItForTheSession()
    {
        _gate.Failed(Sid, Start);
        _gate.Failed(Sid, Start);
        _gate.Failed(Sid, Start);

        Assert.False(_gate.MayAsk(Sid, Start + CacheSettings.OrgRetryAfterRepeatedFailures - TimeSpan.FromSeconds(1)));
        Assert.True(_gate.MayAsk(Sid, Start + CacheSettings.OrgRetryAfterRepeatedFailures));
    }

    [Fact]
    public void TheEarlyRetriesKeepTheirShortWaits()
    {
        _gate.Failed(Sid, Start);
        Assert.True(_gate.MayAsk(Sid, Start + CacheSettings.OrgRetryFirst));

        _gate.Failed(Sid, Start);
        Assert.False(_gate.MayAsk(Sid, Start + CacheSettings.OrgRetryFirst));
        Assert.True(_gate.MayAsk(Sid, Start + CacheSettings.OrgRetrySecond));
    }

    [Fact]
    public void AnAnsweredOrgIsNotAskedAgain()
    {
        _gate.Succeeded(Sid);

        Assert.False(_gate.MayAsk(Sid, Start + TimeSpan.FromDays(1)));
    }
}
