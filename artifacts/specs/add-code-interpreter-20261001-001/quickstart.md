# Quickstart: Code Interpreter Support

Implemented and verified offline and live on Hosted on Anthropic Foundry Sonnet 4.6 on 2026-10-04. See the [package README](../../../src/FYIsoft.Extensions.AI.Anthropic/README.md) and [current SDK status](../../../docs/sdk-status.md) for runnable examples and evidence. The examples below retain the original design walkthrough.

## Prerequisites

Use the repository's .NET 10 SDK and pinned dependencies. Supply an authorized direct Claude or eligible Foundry connection and model. Configure secrets through the caller's existing environment/secret store. A new AI project client is not required by this adapter. Do not assume every Foundry hosting arrangement supports execution or files.

## Enable execution

Illustrative code inside an async method; `sdk` is an existing `IAnthropicClient`, `modelId` is its configured model, and `cancellationToken` comes from the caller.

```csharp
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Anthropic;

IChatClient chat = new AnthropicChatClient(sdk, modelId);
var history = new List<ChatMessage>
{
    new(ChatRole.User,
        "Run code to calculate the sum of squares of the integers 1 through 100.")
};
var options = new ChatOptions
{
    Tools = [new HostedCodeInterpreterTool()],
    AdditionalProperties = new()
    {
        // Application-owned connection identity, never a credential.
        ["anthropic_provider_scope"] = "analysis-connection-01"
    }
};
ChatResponse response = await chat.GetResponseAsync(history, options, cancellationToken);

foreach (AIContent content in response.Messages.SelectMany(m => m.Contents))
{
    if (content is CodeInterpreterToolCallContent call)
    {
        // Render the code/operation from Inputs and metadata; do not execute locally.
    }
    else if (content is CodeInterpreterToolResultContent result)
    {
        // Render result.Outputs and the documented outcome/error metadata.
    }
}
```

A correct executed result is 338350. Tool availability does not guarantee the model chooses execution; the live acceptance check must verify execution content was actually returned.

## Preserve history and continue explicitly

For this adapter's single assistant message, preserve canonical message metadata before saving history. The same step applies after streamed updates are aggregated into a `ChatResponse`. Message-level metadata is authoritative; optional response-level copies are fallback only.

```csharp
ChatMessage assistant = response.Messages.Single();
assistant.AdditionalProperties ??= new();
foreach (string key in new[]
{
    "anthropic_replay_blocks", "anthropic_provider_scope",
    "anthropic_execution_state", "anthropic_stop_reason",
    "anthropic_container_id", "anthropic_container_expires_at"
})
{
    if (!assistant.AdditionalProperties.ContainsKey(key) &&
        response.AdditionalProperties?.TryGetValue(key, out object? value) == true)
        assistant.AdditionalProperties[key] = value;
}
history.Add(assistant);

if (assistant.AdditionalProperties.TryGetValue("anthropic_container_id", out var container))
{
    options.AdditionalProperties!["anthropic_container_id"] = container;
    // Keep the same explicit connection scope and complete message history.
}
history.Add(new(ChatRole.User, "Write the result to a file and share it."));
// Call GetResponseAsync again only after the application decides to continue.
```

Do not persist incomplete streamed history as replayable success. A paused response requires an explicit caller continuation; do not start an unbounded loop. Preserve JSON-valued metadata when serializing history.

## Stream progress

```csharp
await foreach (ChatResponseUpdate update in
    chat.GetStreamingResponseAsync(history, options, cancellationToken))
{
    if (update.AdditionalProperties?.TryGetValue("anthropic_execution_progress", out var progress) == true)
    {
        // Render partial progress as incomplete; never invoke it as a local tool.
    }
    // Collect updates if the application needs ToChatResponse() and durable history.
}
```

Completed calls/results appear once. The terminal update supplies replay blocks and final state. Cancellation throws; unexpected EOF must remain incomplete.

## Analyze and retrieve files

1. Explicitly upload a synthetic CSV using the authorized SDK's `Beta.Files.Upload` with `FileUploadParams` and `BinaryContent`. The SDK supplies the Files beta header.
2. Put the returned ID in `HostedFileContent` on the relevant user message or the hosted tool's Inputs. Set its scope to the same application-owned connection identity; include that identity in request metadata. Ask for known totals and a chart.
3. Find `HostedFileContent` in execution-result Outputs. Request metadata only when the application needs missing name/media type.
4. Explicitly download a generated file with `sdk.Beta.Files.Download(fileId, cancellationToken: cancellationToken)`, then `ReadAsStream(cancellationToken)` on its response. Dispose both objects when finished. Uploaded originals are not the download path.

The message-service-only client constructor cannot expose an underlying file SDK; retain a separate authorized SDK service in that configuration. No adapter-managed download or persistence occurs.

## Validation after implementation

From `C:/dev/SuiteFYI/AISDK/`:

```powershell
dotnet build FYIsoft.Extensions.AI.Providers.slnx
dotnet test FYIsoft.Extensions.AI.Providers.slnx
```

The default suite uses synthetic fixtures and makes no live execution calls. Live tests require explicit enablement and an eligible configured deployment. For each advertised route, record calculation, synthetic CSV analysis, generated-chart download, and explicit continuation/pause behavior. Verify disabled execution, file errors, quota/permission errors, cancellation, scope isolation, history round-trip, and no automatic retry after ambiguous execution.

The original walkthrough was written during planning; current implementation and release evidence are tracked in the status report linked above.
