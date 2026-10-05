# FYIsoft.Extensions.AI.Anthropic

`IChatClient` for the Anthropic API and Anthropic Foundry SDK. Targets .NET 10.

Install from [FYIsoft GitHub Packages](https://github.com/FYIsoft/FYIsoft.Extensions.AI.Providers/blob/main/docs/github-packages.md); see that guide for feed authentication and package commands.

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Anthropic;

// Standard API (reads ANTHROPIC_API_KEY when apiKey is null)
services.AddAnthropicChatClient(apiKey: null, modelId: "claude-sonnet-4-5");

// Foundry: reuse your existing resource connection.
IChatClient chat = new AnthropicChatClient(apiKey, resourceName, modelId);
```

Supports system instructions, images, PDFs, thinking summaries, citations, prompt caching,
application functions, hosted web search, hosted MCP, streaming, usage reporting, and opt-in
hosted code execution. Ordinary chat retains the SDK retry policy plus one
adapter retry on transient 5xx/IO failures before any update has been delivered.

## Thinking, citations, documents, caching, web search, and MCP (0.6.0-preview)

These features work through both the full SDK client and message-service constructors.
They do not require a code execution tool or execution-capable hosting. Provider/model
availability still applies; the adapter forwards provider errors without substituting models.
The six features were live verified on Foundry `claude-sonnet-5`, `claude-sonnet-5-5`,
and `claude-opus-5-5` (version 2) deployments on 2026-10-04.

### Thinking

```csharp
var options = new ChatOptions
{
    MaxOutputTokens = 4096,
    Reasoning = new() { Effort = ReasoningEffort.High, Output = ReasoningOutput.Summary }
};
await foreach (var update in chat.GetStreamingResponseAsync(history, options))
    foreach (var thinking in update.Contents.OfType<TextReasoningContent>())
        Console.Write(thinking.Text);
```

Reasoning requests use adaptive thinking. Low/Medium/High/ExtraHigh map to
`low`/`medium`/`high`/`xhigh`; effort support depends on the model. Effort None sends
`thinking.type=disabled` (models that require thinking can reject this). Output None sends
`display=omitted`; Summary sends `summarized`. Full throws because Claude does not expose
full raw reasoning. To use an older model's manual budget, set
`AnthropicProviderMetadata.ThinkingBudgetTokens` in options to an integer at least 1024 and
less than MaxOutputTokens; the budget replaces adaptive effort control. Do not combine
thinking with incompatible sampling settings or forced tool selection.

Streaming exposes thinking text as it arrives and protected signatures when blocks finish.
Redacted thinking has empty text and opaque ProtectedData. Preserve whole response messages
and metadata for replay; concatenating reasoning strings loses signatures and block ordering.

### PDFs and citations

```csharp
var pdf = new DataContent(await File.ReadAllBytesAsync("report.pdf"), "application/pdf")
{
    AdditionalProperties = new()
    {
        [AnthropicProviderMetadata.CitationsEnabled] = true,
        [AnthropicProviderMetadata.DocumentTitle] = "Quarterly report"
    }
};
var response = await chat.GetResponseAsync(
    [new ChatMessage(ChatRole.User, [pdf, new TextContent("Summarize revenue with citations.")])]);
foreach (var citation in response.Messages.SelectMany(m => m.Contents)
    .SelectMany(c => c.Annotations ?? []).OfType<CitationAnnotation>())
    Console.WriteLine($"{citation.Title}: {citation.Url} {citation.Snippet}");
```

PDF DataContent becomes a base64 document. HTTPS `UriContent` with media type
`application/pdf` becomes a provider-fetched URL document; the adapter makes no local fetch.
`text/plain` DataContent becomes a text document. Optional DocumentContext provides document
context. HostedFileContent remains an uploaded **code execution** input, not a PDF document.

Text annotations are standard `CitationAnnotation`, including title, URL, snippet, and web
search tool attribution. Original page/character/document indexes and encrypted references
are retained in `citation.AdditionalProperties[AnthropicProviderMetadata.Citation]` as JSON.
Those indexes locate source text, not positions in the generated answer. Streaming emits
annotations when the cited text block finishes, with `content_block_index` on the update;
aggregate with `ToChatResponseAsync()` to retain them without repeating answer text.

### Prompt caching

```csharp
var cache = new { type = "ephemeral", ttl = "5m" }; // or "1h"
var options = new ChatOptions
{
    AdditionalProperties = new() { [AnthropicProviderMetadata.CacheControl] = cache }
};
```

The options value enables provider automatic caching. For explicit breakpoints, put the
same key on content (including system TextContent) or tool AdditionalProperties. Thinking
blocks cannot have explicit breakpoints. Provider minimum prefix lengths, breakpoint limits,
TTL ordering, and cache pricing apply; a cache request does not guarantee a hit.

Usage InputTokenCount includes uncached input, cache reads, and cache creation.
CachedInputTokenCount reports reads; AdditionalCounts includes
`anthropic_cache_creation_input_tokens`, `anthropic_uncached_input_tokens`, and any
`anthropic_ephemeral_5m_input_tokens`/`anthropic_ephemeral_1h_input_tokens` breakdown.
ReasoningTokenCount is set when the provider returns thinking token details. Streaming merges
usage snapshots before emitting one final UsageContent.

### Hosted web search and MCP

```csharp
var search = new HostedWebSearchTool(new Dictionary<string, object?>
{
    [AnthropicProviderMetadata.WebSearchOptions] = new { max_uses = 3 }
});
var docs = new HostedMcpServerTool("microsoft_learn", "https://learn.microsoft.com/api/mcp")
{
    AllowedTools = ["microsoft_docs_search"],
    ApprovalMode = HostedMcpServerToolApprovalMode.NeverRequire
};
var response = await chat.GetResponseAsync(history, new ChatOptions { Tools = [search, docs] });
```

Web search uses `web_search_20250305`, with optional max_uses, allowed_domains,
blocked_domains, and user_location in WebSearchOptions. Allowed and blocked domains are
mutually exclusive. Responses expose WebSearchToolCallContent/ResultContent and citation links.

MCP uses the provider connector with the `mcp-client-2025-11-20` beta header. Server addresses
must be publicly reachable HTTPS endpoints. AllowedTools null enables all tools; an empty
list enables none. Only an Authorization Bearer header can be translated to the connector's
authorization_token. Other headers, ServerDescription, and approval modes requiring an
approval round trip throw before sending. Null approval mode follows provider automatic
execution; set NeverRequire explicitly when that is intended. For interactive approval,
use a client-side MCP integration. Tokens are request-only and are not copied to response metadata.

MCP calls/results become McpServerToolCallContent/ResultContent with call ID, server, tool
arguments, outputs, and `AnthropicProviderMetadata.IsError`. Neither MCP nor search executes
locally. Both retry layers are disabled for MCP-bearing requests to avoid repeating remote
side effects. Unknown tool declarations throw rather than disappearing.

### Saving rich responses

Use the same history rules described below for thinking, citations, search, and MCP.
Non-execution rich responses use `anthropic_response_state` instead of
`anthropic_execution_state`. Incomplete streams are rejected on replay. The terminal
message's ordered replay collection preserves provider signatures, citations, and hosted
tool results through standard ChatMessage JSON serialization, including when generic stream
aggregation combines text. Reuse the client, or supply a stable ProviderScope for the same
authorized connection when restoring into a new client. Do not strip AdditionalProperties.

Provider references: [thinking](https://platform.claude.com/docs/en/build-with-claude/thinking),
[citations](https://platform.claude.com/docs/en/build-with-claude/citations),
[PDFs](https://platform.claude.com/docs/en/build-with-claude/pdf-support),
[caching](https://platform.claude.com/docs/en/build-with-claude/prompt-caching),
[web search](https://platform.claude.com/docs/en/agents-and-tools/tool-use/web-search-tool),
[MCP connector](https://platform.claude.com/docs/en/agents-and-tools/mcp-connector).

## Hosted code execution (0.5.0-preview)

The adapter maps `HostedCodeInterpreterTool` to `code_execution_20250825`. Execution runs
on the provider. The adapter never executes returned commands locally.

```csharp
// sdk is your existing IAnthropicClient; modelId must support the tool profile.
IChatClient chat = new AnthropicChatClient(sdk, modelId);
var options = new ChatOptions
{
    Tools = [new HostedCodeInterpreterTool()],
    AdditionalProperties = new()
    {
        // A nonsecret identity for this authorized connection; persist it with history.
        [AnthropicCodeExecutionMetadata.ProviderScope] = "analysis-connection-01"
    }
};
var history = new List<ChatMessage>
{
    new(ChatRole.User, "Run code to calculate the sum of squares from 1 through 100.")
};
var response = await chat.GetResponseAsync(history, options);
var assistant = response.Messages.Single();
foreach (var result in assistant.Contents.OfType<CodeInterpreterToolResultContent>())
{
    Console.WriteLine(result.AdditionalProperties![AnthropicCodeExecutionMetadata.Outcome]);
    // result.Outputs contains named stdout/stderr DataContent and generated HostedFileContent.
}
```

Calls expose their provider call ID, operation, complete JSON input and language-appropriate
`Inputs`. Results preserve stdout, stderr (including empty strings), return code and error code
when supplied. Ordinary functions remain `FunctionCallContent`; unknown blocks remain inspectable
`AIContent` with provider JSON. Auto/None/Required/named function selection is preserved.

Streaming emits text deltas normally, metadata-only `anthropic_execution_progress` fragments,
and one typed call/result per completed block. Collect updates with `ToChatResponseAsync()`
or `ToChatResponse()` to preserve the terminal message metadata. Progress fragments are not
complete JSON. Unknown delta semantics are returned as `anthropic_raw_stream_event` diagnostics
and make the message incomplete; the adapter does not guess how to replay them.

## History and explicit continuation

The assistant message's `AdditionalProperties` contains `anthropic_replay_blocks`,
`anthropic_provider_scope`, `anthropic_execution_state`, `anthropic_stop_reason`, and any
container ID/expiry supplied by the provider. Preserve these fields when serializing
`ChatMessage` history with `System.Text.Json`; no SDK objects are needed to restore it.

```csharp
history.Add(assistant);
options.AdditionalProperties![AnthropicCodeExecutionMetadata.ContainerId] =
    assistant.AdditionalProperties![AnthropicCodeExecutionMetadata.ContainerId];
history.Add(new(ChatRole.User, "Write the previous result to a file and share it."));
var continued = await chat.GetResponseAsync(history, options);
```

Only set a container ID when the response supplied one. It requires an explicit matching
provider scope. `ChatOptions.ConversationId` is not used for containers. Without an explicit
scope, a client has an immutable private scope, so history cannot accidentally cross client
instances. Stable caller-supplied scopes must remain bound to the same authorized connection;
they do not implement tenant authorization. Container expiry is informational; provider errors
remain available to callers. The client keeps no last conversation or container.

A `pause_turn` has execution state `paused` and finish reason `pause_turn`. Continuation is a
caller decision. Missing `message_stop`, malformed input, unknown deltas and length truncation
remain incomplete; cancellation throws. Incomplete history is rejected on replay.

Message replay JSON is authoritative and independently cloned. Editing display text or nested
outputs does not edit replay. To change history, replace the message, or remove/rebuild both
its replay collection and affected per-content `anthropic_raw_content_block` envelopes.
Unknown complete blocks and provider thinking/signatures survive replay.

Execution-bearing requests (including continuation or replay without a new hosted declaration)
disable both SDK and adapter retries. Transport failure can leave the remote outcome unknown.
Caller-installed retry middleware remains outside this guarantee. Default adapter logs omit
exception bodies, code, outputs and file contents.

## Files

Upload and download explicitly through the existing `IAnthropicClient.Beta.Files` service.
The SDK supplies its Files beta header. A message-service-only adapter does not expose a file
client; retain your own authorized SDK in that configuration.

```csharp
using Anthropic.Core;
using Anthropic.Models.Beta.Files;

using var source = File.OpenRead("data.csv");
var uploaded = await sdk.Beta.Files.Upload(new FileUploadParams
{
    File = new BinaryContent { Stream = source, FileName = "data.csv" }
});
((HostedCodeInterpreterTool)options.Tools![0]).Inputs = [new HostedFileContent(uploaded.ID)];
// The explicit provider scope in options binds this newly uploaded reference.
var analysis = await chat.GetResponseAsync(history, options);
var generated = analysis.Messages.SelectMany(m => m.Contents)
    .OfType<CodeInterpreterToolResultContent>()
    .SelectMany(r => r.Outputs ?? []).OfType<HostedFileContent>().First();
using var download = await sdk.Beta.Files.Download(generated.FileId);
await using var bytes = await download.ReadAsStream();
// Copy bytes to caller-owned storage if desired; dispose the response and stream.
```

Tool inputs accept uploaded `HostedFileContent` only. They are appended to the last user
message and deduplicated there by file ID without mutating caller history. Direct file
references retain their positions. References from another scope/provider fail locally.
No automatic upload, download, metadata lookup, temporary storage or deletion occurs.

## Deployment evidence and live checks

Offline tests cover direct and message-service SDK construction, request JSON, streamed/completed
content, serialization, file operations and HTTP request counts. On 2026-10-04, live ordinary
chat, streaming, named-function selection and tool-result continuation passed on the configured
Foundry connection with `claude-sonnet-5`. That deployment is version 2 (Hosted on Azure) and rejects
hosted execution. **All four live tests subsequently passed on the existing `claude-sonnet-4-6`
version 1 (Hosted on Anthropic) deployment**, including calculation, CSV analysis, PNG download and
serialized container continuation. Use that deployment name for execution requests on this resource.
The initial failure did not require a workspace toggle or new resource.

Packages remain preview; live evidence applies to this verified Foundry route. Direct Anthropic was
not tested. Versions: Anthropic 12.29.0, Foundry 0.7.0, Microsoft.Extensions.AI 10.7.0.

The initial profile implements Bash/file operations, not persistent Python variable bindings or
programmatic invocation of application functions. See the [Anthropic tool documentation](https://platform.claude.com/docs/en/agents-and-tools/tool-use/code-execution-tool).
The adapter requires no additional Foundry project client. Set optional `anthropic_hosting` to
`direct`, `foundry-anthropic`, or `foundry-azure`. The last is rejected locally for hosted execution;
omitted hosting lets the provider decide. See [Foundry hosting capabilities](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/claude-models).
There is no provider fallback or automatic provisioning.

Configure secrets in your environment, then explicitly opt in:

```powershell
$env:ANTHROPIC_LIVE_TESTS = '1'
$env:ANTHROPIC_TEST_MODEL = 'claude-sonnet-4-6' # Verified deployment on our Foundry resource.
# ANTHROPIC_API_KEY must already be configured.
# For Foundry also set ANTHROPIC_FOUNDRY_RESOURCE; leave it unset for direct API.
dotnet test tests/FYIsoft.Extensions.AI.Anthropic.Tests -c Release --filter 'Category=Live' --logger trx
```

The live tests cover ordinary text/streaming, function selection/continuation, an executed calculation,
uploaded synthetic CSV totals, a downloaded PNG, and serialized history with explicit container
continuation. They use paid provider APIs and
clean up their synthetic upload. Live output records the route, model, versions and UTC time.
Record that evidence before advertising a route as supported.
