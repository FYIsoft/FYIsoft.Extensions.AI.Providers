using System.Net;
using System.Text;
using Anthropic;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Anthropic;
using Microsoft.Extensions.AI.Jev;

using var anthropicHttp = new HttpClient(new ResponseHandler("""{"id":"msg_smoke","type":"message","role":"assistant","model":"model","content":[{"type":"text","text":"package smoke"},{"type":"server_tool_use","id":"srv_smoke","name":"code_execution","input":{"code":"print(42)"}},{"type":"code_execution_tool_result","tool_use_id":"srv_smoke","content":{"type":"code_execution_result","stdout":"42","stderr":"","return_code":0,"content":[]}}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}"""));
using IChatClient chat = new AnthropicChatClient(new AnthropicClient { ApiKey = "synthetic", HttpClient = anthropicHttp }, "model");
var response = await chat.GetResponseAsync("compute", new ChatOptions { Tools = [new HostedCodeInterpreterTool()] });
if (response.Text != "package smoke" || response.Messages.Single().Contents.OfType<CodeInterpreterToolResultContent>().Single().AdditionalProperties![AnthropicCodeExecutionMetadata.Stdout]?.ToString() != "42") throw new Exception("Anthropic packaged API failed.");
using var jevHttp = new HttpClient(new ResponseHandler("""{"model":"jev-test","answers":{"q":{"type":"noul","noul":0.9}},"usage":{"input_tokens":1,"output_tokens":1}}"""));
using var jev = new JevClient(new JevClientOptions { ApiKey = "synthetic" }, jevHttp);
var judgement = await jev.SystemOneAsync("state", new Dictionary<string, JevQuestion> { ["q"] = JevQuestion.Noul("yes?") });
if (!judgement.GetAnswer<JevNoulAnswer>("q").IsYes) throw new Exception("Jev packaged API failed.");
using var richHttp = new HttpClient(new ResponseHandler("""{"id":"msg_rich","type":"message","role":"assistant","model":"model","content":[{"type":"thinking","thinking":"Reviewing the report.","signature":"synthetic-signature"},{"type":"text","text":"Revenue is 42.","citations":[{"type":"web_search_result_location","url":"https://example.com","title":"Report","cited_text":"Revenue is 42.","encrypted_index":"synthetic"}]}],"stop_reason":"end_turn","stop_sequence":null,"usage":{"input_tokens":1,"cache_read_input_tokens":20,"output_tokens":5}}"""));
using IChatClient rich = new AnthropicChatClient(new AnthropicClient { ApiKey = "synthetic", HttpClient = richHttp }, "model");
var richOptions = new ChatOptions {
    Reasoning = new() { Effort = ReasoningEffort.High, Output = ReasoningOutput.Summary },
    Tools = [new HostedWebSearchTool(), new HostedMcpServerTool("docs", "https://example.com/mcp") { AllowedTools = ["lookup"], ApprovalMode = HostedMcpServerToolApprovalMode.NeverRequire }],
    AdditionalProperties = new() { [AnthropicProviderMetadata.CacheControl] = new { type = "ephemeral" } }
};
var pdf = new DataContent(Encoding.ASCII.GetBytes("%PDF-fixture"), "application/pdf") { AdditionalProperties = new() { [AnthropicProviderMetadata.CitationsEnabled] = true } };
var richResult = await rich.GetResponseAsync([new ChatMessage(ChatRole.User, [pdf, new TextContent("Summarize")])], richOptions);
if (richResult.Messages.Single().Contents.OfType<TextReasoningContent>().Single().ProtectedData != "synthetic-signature" ||
    richResult.Messages.Single().Contents.OfType<TextContent>().Single().Annotations!.OfType<CitationAnnotation>().Single().Url!.Host != "example.com" ||
    richResult.Usage!.CachedInputTokenCount != 20) throw new Exception("Rich provider packaged API failed.");
Console.WriteLine("PASS: both installed NuGet SDKs executed; Anthropic 0.6 rich options, PDF, reasoning, citations and cached usage verified.");

sealed class ResponseHandler(string response) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") });
}
