using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;

namespace Microsoft.Extensions.AI.Decision1.Tests;

internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? ContentType, string Body)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement.Clone();
}

/// <summary>A synthetic transport: records every request and answers from a script.</summary>
internal sealed class FakeDecision1Handler : HttpMessageHandler
{
    private readonly Func<int, CancellationToken, Task<HttpResponseMessage>> _respond;

    public FakeDecision1Handler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

    public FakeDecision1Handler(params ProbeFixture[] script)
        : this((n, _) => Task.FromResult(Response(script[Math.Min(n, script.Length) - 1])))
    {
    }

    public List<RecordedRequest> Requests { get; } = [];

    public RecordedRequest Last => Requests[^1];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        int count;
        lock (Requests)
        {
            Requests.Add(new(request.Method, request.RequestUri!, headers, request.Content?.Headers.ContentType?.MediaType, body));
            count = Requests.Count;
        }
        return await _respond(count, cancellationToken);
    }

    public static HttpResponseMessage Response(ProbeFixture fixture, IReadOnlyDictionary<string, string>? extraHeaders = null)
    {
        var response = new HttpResponseMessage((HttpStatusCode)fixture.Status)
        {
            Content = new StringContent(fixture.Body, Encoding.UTF8, "application/json"),
        };
        foreach (var (name, value) in fixture.Headers)
            response.Headers.TryAddWithoutValidation(name, value);
        if (extraHeaders is not null)
            foreach (var (name, value) in extraHeaders)
                response.Headers.TryAddWithoutValidation(name, value);
        return response;
    }
}

/// <summary>A credential that counts calls and issues tokens with a configurable lifetime.</summary>
internal sealed class FakeTokenCredential(TimeProvider time, TimeSpan lifetime) : TokenCredential
{
    public int Calls;
    public List<string[]> Scopes { get; } = [];

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        await Task.Yield();
        cancellationToken.ThrowIfCancellationRequested();
        var n = Interlocked.Increment(ref Calls);
        lock (Scopes)
            Scopes.Add(requestContext.Scopes);
        return new AccessToken($"synthetic-token-{n}", time.GetUtcNow() + lifetime);
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Builds clients over a synthetic transport with recorded, instant retry delays.</summary>
internal sealed class TestClient : IDisposable
{
    public const string Endpoint = "https://synthetic-resource.services.ai.azure.com";
    public const string Deployment = "decision-1-test";
    public const string ApiKey = "synthetic-api-key";

    private TestClient(FakeDecision1Handler handler, Decision1Client client, Decision1ClientCore core)
    {
        Handler = handler;
        Client = client;
        Core = core;
    }

    public FakeDecision1Handler Handler { get; }
    public Decision1Client Client { get; }
    public Decision1ClientCore Core { get; }
    public List<TimeSpan> Delays { get; } = [];

    public static Decision1ClientOptions Options(Action<Decision1ClientOptions>? configure = null)
    {
        var options = new Decision1ClientOptions
        {
            Endpoint = new Uri(Endpoint),
            Deployment = Deployment,
            ApiKey = ApiKey,
            UseEnvironmentVariables = false,
        };
        configure?.Invoke(options);
        return options;
    }

    public static TestClient Create(FakeDecision1Handler handler, Action<Decision1ClientOptions>? configure = null, TimeProvider? time = null)
    {
        var core = new Decision1ClientCore(Options(configure).Resolve(), time);
        var client = new Decision1Client(new HttpClient(handler), core, logger: null, disposeHttpClient: true);
        var test = new TestClient(handler, client, core);
        core.NextJitter = () => 0;
        core.DelayAsync = (delay, ct) =>
        {
            test.Delays.Add(delay);
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        return test;
    }

    public void Dispose() => Client.Dispose();
}

internal static class Questions
{
    public static Dictionary<string, Decision1Question> Combined() => new()
    {
        ["team"] = Decision1Question.Choice("Which team should handle this request?", new Dictionary<string, string>
        {
            ["billing"] = "Charges, invoices, and refunds",
            ["engineering"] = "Bugs, errors, and outages",
            ["support"] = "How-to and account questions",
        }),
        ["severity"] = Decision1Question.Score("How severe is this issue?", "Cosmetic", "Minor", "Major", "Critical"),
        ["repeat_contact"] = Decision1Question.Noul("Has the customer contacted support before?"),
        ["refund"] = Decision1Question.Noul("Does the message ask for a refund?", "Asks for money back", "Does not ask for money back"),
    };

    public static Dictionary<string, Decision1Question> SingleNoul() => new()
    {
        ["q"] = Decision1Question.Noul("Is it?"),
    };
}
