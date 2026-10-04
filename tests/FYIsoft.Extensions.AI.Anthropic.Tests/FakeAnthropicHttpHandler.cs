using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.AI.Anthropic.Tests;

internal sealed class FakeAnthropicHttpHandler : HttpMessageHandler
{
    public List<JsonElement> Requests { get; } = [];
    public List<string[]> BetaHeaders { get; } = [];
    public Func<int, HttpResponseMessage> Respond { get; set; } = _ => Json(ExecutionFixtures.Response);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone());
        BetaHeaders.Add(request.Headers.TryGetValues("anthropic-beta", out var beta) ? beta.ToArray() : []);
        return Respond(Requests.Count);
    }
    public AnthropicClient Sdk(int retries = 2) => new()
    {
        ApiKey = "synthetic-test-key", BaseUrl = "https://test.invalid", HttpClient = new HttpClient(this), MaxRetries = retries
    };
    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Sse(IEnumerable<string> events) => new(HttpStatusCode.OK)
    { Content = new StringContent(string.Concat(events.Select(e => $"event: {JsonDocument.Parse(e).RootElement.GetProperty("type").GetString()}\ndata: {e}\n\n")), Encoding.UTF8, "text/event-stream") };
}

internal static class ExecutionFixtures
{
    public const string Call = """{"type":"server_tool_use","id":"srv_1","name":"bash_code_execution","input":{"command":"printf '338350\\n'"},"future_field":{"keep":true}}""";
    public const string Result = """{"type":"bash_code_execution_tool_result","tool_use_id":"srv_1","content":{"type":"bash_code_execution_result","stdout":"338350\n","stderr":"","return_code":0,"content":[{"type":"bash_code_execution_output","file_id":"file_chart"}]}}""";
    public static readonly string[] Blocks = [
        """{"type":"text","text":"Calculating."}""",
        """{"type":"thinking","thinking":"private reasoning","signature":"signature-test"}""",
        Call, Result,
        """{"type":"future_block","payload":{"keep":"unknown"}}""",
        """{"type":"tool_use","id":"tool_1","name":"lookup","input":{"id":7}}"""
    ];
    public static string Response => Message(Blocks);
    public static string Message(IEnumerable<string> blocks, string reason = "end_turn", string id = "msg_test", string container = "container_test") =>
        $$$"""{"id":"{{{id}}}","type":"message","role":"assistant","model":"claude-test","content":[{{{string.Join(',', blocks)}}}],"stop_reason":"{{{reason}}}","stop_sequence":null,"container":{"id":"{{{container}}}","expires_at":"2026-10-05T00:00:00Z"},"usage":{"input_tokens":12,"output_tokens":34}}""";
    public static string Start => $$$"""{"type":"message_start","message":{{{Message([], "end_turn")}}}}""";
    public static string BlockStart(int index, string block) => $$$"""{"type":"content_block_start","index":{{{index}}},"content_block":{{{block}}}}""";
    public static string Delta(int index, string type, string key, string value) => JsonSerializer.Serialize(new
    { type = "content_block_delta", index, delta = new Dictionary<string, object> { ["type"] = type, [key] = value } });
    public static string BlockStop(int index) => $$$"""{"type":"content_block_stop","index":{{{index}}}}""";
    public static string StopDelta(string reason = "end_turn") => $$$"""{"type":"message_delta","delta":{"stop_reason":"{{{reason}}}"},"usage":{"output_tokens":34}}""";
    public const string Stop = """{"type":"message_stop"}""";
    public static IEnumerable<string> Events(string reason = "end_turn")
    {
        yield return Start;
        for (int i = 0; i < Blocks.Length; i++)
        {
            yield return BlockStart(i, Blocks[i]);
            yield return BlockStop(i);
        }
        yield return StopDelta(reason);
        yield return Stop;
    }
    public static ChatOptions Options() => new()
    {
        Tools = [new HostedCodeInterpreterTool()],
        AdditionalProperties = new() { [AnthropicCodeExecutionMetadata.ProviderScope] = "test-connection" }
    };
    public static List<ChatMessage> History() => [new(ChatRole.User, "Calculate and make a chart")];
}

internal sealed class RecordingLogger : ILogger
{
    public List<string> Entries { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add(formatter(state, exception) + exception?.ToString());
}

