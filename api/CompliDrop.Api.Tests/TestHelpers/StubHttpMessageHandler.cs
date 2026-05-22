using System.Net;
using System.Text;

namespace CompliDrop.Api.Tests.TestHelpers;

/// <summary>
/// Records the last request (including its fully-buffered body) and returns canned responses, so the
/// tests can drive the real Gemini/Anthropic clients against synthetic provider replies with no network
/// I/O. The body is read into <see cref="LastRequestBody"/> *during* SendAsync because the clients wrap
/// the request in <c>using</c> and dispose its content once the call returns; request headers and URI
/// are not disposed, so <see cref="LastRequest"/> stays safe to inspect afterwards.
/// </summary>
public sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    private readonly Func<HttpRequestMessage, string, HttpResponseMessage>? _responder;

    /// <summary>Handler with no canned response — callers must <see cref="Enqueue"/> before use.</summary>
    public StubHttpMessageHandler() { }

    /// <summary>Handler that returns a single canned response.</summary>
    public StubHttpMessageHandler(HttpStatusCode status, string jsonBody) => Enqueue(status, jsonBody);

    /// <summary>Handler that computes its response from the request (e.g. to vary per call).</summary>
    public StubHttpMessageHandler(Func<HttpRequestMessage, string, HttpResponseMessage> responder)
        => _responder = responder;

    public HttpRequestMessage? LastRequest { get; private set; }
    public string LastRequestBody { get; private set; } = string.Empty;
    public int CallCount { get; private set; }

    public StubHttpMessageHandler Enqueue(HttpStatusCode status, string jsonBody)
    {
        _responses.Enqueue(new HttpResponseMessage(status)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json"),
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;
        LastRequestBody = request.Content is null
            ? string.Empty
            : await request.Content.ReadAsStringAsync(cancellationToken);

        if (_responder is not null)
            return _responder(request, LastRequestBody);
        if (_responses.Count > 0)
            return _responses.Dequeue();

        throw new InvalidOperationException(
            "StubHttpMessageHandler received a request but had no response queued.");
    }
}

/// <summary>
/// Hands out <see cref="HttpClient"/>s bound to a single <see cref="HttpMessageHandler"/> regardless of
/// the requested client name, so the extraction clients (which call <c>CreateClient("google")</c> /
/// <c>CreateClient("anthropic")</c>) resolve to the stub. <c>disposeHandler:false</c> keeps the shared
/// handler alive across multiple <see cref="CreateClient"/> calls.
/// </summary>
public sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
