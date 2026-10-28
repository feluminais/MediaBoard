using System.Net;

namespace MediaBoard.Core.Tests.Support;

/// <summary>
/// A fake <see cref="HttpMessageHandler"/> for status codes and network faults, which no recorded fixture
/// can express. The responder gets the 1-based call number and may return a response or throw.
/// </summary>
public sealed class StubHandler(Func<int, HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public int Calls { get; private set; }

    public static StubHandler Status(HttpStatusCode status) => new((_, _) => new HttpResponseMessage(status));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult(responder(Calls, request));
    }
}
