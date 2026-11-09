using System.Collections.Concurrent;

namespace Iris.Tests.Support;

public sealed class StubHttpHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _respond;

    public StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) => _respond = respond;

    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        return _respond(request);
    }
}
