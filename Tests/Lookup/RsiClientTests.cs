using System.Net;
using Iris.Core.Lookup;
using Iris.Tests.Support;
using Xunit;

namespace Iris.Tests.Lookup;

public class RsiClientTests
{
    private const int Burst = 5;

    private readonly FakeClock _clock = new();
    private readonly List<TimeSpan> _delays = new();

    private RsiRateLimiter Limiter() => new(_clock, (wait, _) =>
    {
        lock (_delays)
            _delays.Add(wait);
        _clock.Advance(wait);
        return Task.CompletedTask;
    }, TimeSpan.FromSeconds(1), Burst);

    private static Task<HttpResponseMessage> NotFound(HttpRequestMessage _) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

    private RsiClient Client(StubHttpHandler handler, RsiRateLimiter limiter) => new(new HttpClient(handler), limiter, _clock);

    private static StubHttpHandler AnswerOnceAllArrive(int expected, Func<HttpResponseMessage> respond)
    {
        var arrived = 0;
        var everyone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return new StubHttpHandler(async _ =>
        {
            if (Interlocked.Increment(ref arrived) == expected)
                everyone.SetResult();
            await everyone.Task;
            return respond();
        });
    }

    private async Task<int> RequestsAfterMembersCalls(string body)
    {
        var handler = new StubHttpHandler(request => Task.FromResult(request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound)));
        var client = Client(handler, Limiter());
        for (var i = 0; i < CacheSettings.RsiBreakerThreshold + 1; i++)
        {
            await client.GetOrgMembersAsync("ORG" + i);
            _clock.Advance(CacheSettings.RsiDefaultInterval + TimeSpan.FromSeconds(1));
        }

        var before = handler.Requests.Count;
        await client.GetPlayerOrgsAsync("PLAYER1");
        return handler.Requests.Count - before;
    }

    [Fact]
    public async Task ARefusalFromTheMembersApiDoesNotPauseAllLookups()
    {
        Assert.Equal(1, await RequestsAfterMembersCalls("{\"success\":0,\"msg\":\"not allowed\"}"));
    }

    [Fact]
    public async Task AMembersAnswerWithNoSuccessFieldStillPausesLookups()
    {
        Assert.Equal(0, await RequestsAfterMembersCalls("{\"data\":{}}"));
    }

    [Fact]
    public async Task ARequestWaitingForItsTurnIsNotSentOnceLookupsPause()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delays = 0;
        var limiter = new RsiRateLimiter(_clock, (wait, _) =>
        {
            if (Interlocked.Increment(ref delays) == 1)
                return gate.Task;
            _clock.Advance(wait);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(1), Burst);
        var handler = new StubHttpHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var client = Client(handler, limiter);
        await client.GetPlayerOrgsAsync("PLAYER0");
        var waiting = client.GetPlayerOrgsAsync("WAITER");
        for (var i = 1; i < CacheSettings.RsiBreakerThreshold; i++)
        {
            _clock.Advance(CacheSettings.RsiDefaultInterval + TimeSpan.FromSeconds(1));
            await client.GetPlayerOrgsAsync("PLAYER" + i);
        }

        var sent = handler.Requests.Count;
        gate.SetResult();
        await waiting;

        Assert.Equal(sent, handler.Requests.Count);
    }

    [Fact]
    public async Task AnUnreadableMembersAnswerStillPausesLookups()
    {
        Assert.Equal(0, await RequestsAfterMembersCalls("<html>changed layout</html>"));
    }

    [Fact]
    public async Task RequestsAskForHttp2AndFallBackWhenTheServerCannot()
    {
        var handler = new StubHttpHandler(NotFound);

        await Client(handler, Limiter()).GetPlayerOrgsAsync("PLAYER1");

        Assert.True(handler.Requests.TryPeek(out var request));
        Assert.Equal(HttpVersion.Version20, request!.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, request.VersionPolicy);
    }

    [Fact]
    public void TheHandlerKeepsOneConnectionWarmBetweenLookups()
    {
        using var handler = RsiClient.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.All, handler.AutomaticDecompression);
        Assert.Equal(HttpKeepAlivePingPolicy.Always, handler.KeepAlivePingPolicy);
        Assert.True(handler.KeepAlivePingDelay > TimeSpan.Zero && handler.KeepAlivePingDelay < handler.PooledConnectionIdleTimeout);
        Assert.True(handler.PooledConnectionIdleTimeout >= TimeSpan.FromMinutes(1));
        Assert.True(handler.PooledConnectionLifetime > handler.PooledConnectionIdleTimeout, "a finite lifetime lets DNS changes take effect");
        Assert.False(handler.EnableMultipleHttp2Connections);
    }

    [Fact]
    public async Task LiveLookupsUseTheBurst()
    {
        var client = Client(new StubHttpHandler(NotFound), Limiter());

        await Task.WhenAll(Enumerable.Range(0, Burst).Select(i => client.GetPlayerOrgsAsync($"PLAYER{i}", default, RsiLane.Live)));

        Assert.Empty(_delays);
    }

    [Fact]
    public async Task BackgroundLookupsAreTheDefaultAndStayPaced()
    {
        var client = Client(new StubHttpHandler(NotFound), Limiter());

        for (var i = 0; i < 3; i++)
            await client.GetPlayerOrgsAsync($"PLAYER{i}");

        Assert.Equal(2, _delays.Count);
    }

    [Fact]
    public async Task LiveOrgAndProfileRequestsCanAlsoBurst()
    {
        var client = Client(new StubHttpHandler(NotFound), Limiter());

        await client.GetOrgInfoAsync("ORGA", default, RsiLane.Live);
        await client.GetPlayerProfileAsync("PLAYER1", default, RsiLane.Live);

        Assert.Empty(_delays);
    }

    [Fact]
    public async Task TheClientReportsHowManyLiveTurnsAreFree()
    {
        var client = Client(new StubHttpHandler(NotFound), Limiter());
        Assert.Equal(Burst, client.LiveTurnsAvailable);

        await client.GetPlayerOrgsAsync("PLAYER1", default, RsiLane.Live);

        Assert.Equal(Burst - 1, client.LiveTurnsAvailable);
    }

    [Fact]
    public async Task OneThrottledBurstBacksOffOnce()
    {
        var limiter = Limiter();
        var handler = AnswerOnceAllArrive(Burst, () => new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        var client = Client(handler, limiter);

        await Task.WhenAll(Enumerable.Range(0, Burst).Select(i => client.GetPlayerOrgsAsync($"PLAYER{i}", default, RsiLane.Live)));

        Assert.Equal(TimeSpan.FromSeconds(2), limiter.CurrentGap);
    }

    [Fact]
    public async Task OneForbiddenBurstDoesNotPauseAllLookups()
    {
        var arrived = 0;
        var everyone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpHandler(async _ =>
        {
            var arrival = Interlocked.Increment(ref arrived);
            if (arrival > Burst)
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            if (arrival == Burst)
                everyone.SetResult();
            await everyone.Task;
            return new HttpResponseMessage(HttpStatusCode.Forbidden);
        });
        var client = Client(handler, Limiter());
        await Task.WhenAll(Enumerable.Range(0, Burst).Select(i => client.GetPlayerOrgsAsync($"PLAYER{i}", default, RsiLane.Live)));

        var result = await client.GetPlayerOrgsAsync("LATER1", default, RsiLane.Live);

        Assert.Equal(RsiStatus.NotFound, result.Status);
        Assert.Equal(Burst + 1, handler.Requests.Count);
    }

    [Fact]
    public async Task OneBurstOfNetworkFailuresBacksOffOnce()
    {
        var limiter = Limiter();
        var handler = AnswerOnceAllArrive(Burst, () => throw new HttpRequestException("connection reset"));
        var client = Client(handler, limiter);

        await Task.WhenAll(Enumerable.Range(0, Burst).Select(i => client.GetPlayerOrgsAsync($"PLAYER{i}", default, RsiLane.Live)));

        Assert.Equal(TimeSpan.FromSeconds(2), limiter.CurrentGap);
    }

    [Fact]
    public async Task AThrottledBurstStopsFurtherBursting()
    {
        var limiter = Limiter();
        var client = Client(AnswerOnceAllArrive(Burst, () => new HttpResponseMessage(HttpStatusCode.TooManyRequests)), limiter);
        await Task.WhenAll(Enumerable.Range(0, Burst).Select(i => client.GetPlayerOrgsAsync($"PLAYER{i}", default, RsiLane.Live)));
        _delays.Clear();

        var next = Client(new StubHttpHandler(NotFound), limiter);
        await next.GetPlayerOrgsAsync("LATER1", default, RsiLane.Live);
        await next.GetPlayerOrgsAsync("LATER2", default, RsiLane.Live);

        Assert.Equal(2, _delays.Count);
    }
}
