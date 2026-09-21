using System.Net;
using System.Text;

namespace Microsoft.Extensions.AI.Jev.Tests;

/// <summary>Replays queued responses and records every request.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> _responses = new();

    public List<RecordedRequest> Requests { get; } = [];

    public FakeHttpHandler Respond(HttpStatusCode status, string? json = null, Action<HttpResponseMessage>? configure = null)
    {
        _responses.Enqueue(_ =>
        {
            var response = new HttpResponseMessage(status);
            if (json is not null)
            {
                response.Content = new StringContent(json, Encoding.UTF8, "application/json");
            }

            configure?.Invoke(response);
            return Task.FromResult(response);
        });
        return this;
    }

    public FakeHttpHandler Throw(Exception exception)
    {
        _responses.Enqueue(_ => Task.FromException<HttpResponseMessage>(exception));
        return this;
    }

    public FakeHttpHandler Hang()
    {
        _responses.Enqueue(async _ =>
        {
            await Task.Delay(Timeout.Infinite);
            throw new InvalidOperationException();
        });
        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new RecordedRequest(request.Method, request.RequestUri!, request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase), body));

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No response queued.");
        }

        return await _responses.Dequeue()(request).WaitAsync(cancellationToken);
    }

    public static JevClient CreateClient(FakeHttpHandler handler, Action<JevClientOptions>? configure = null)
    {
        var options = new JevClientOptions
        {
            ApiKey = "test-key",
            BaseUrl = new Uri("https://api.test.local/"),
            Retry = new JevRetryPolicy { InitialBackoff = TimeSpan.FromMilliseconds(1), MaxBackoff = TimeSpan.FromMilliseconds(5) },
        };
        configure?.Invoke(options);
        return new JevClient(options, new HttpClient(handler));
    }
}

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body);
