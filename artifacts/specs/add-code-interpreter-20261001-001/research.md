# Research: Anthropic Hosted Code Interpreter

**Date**: 2026-10-01. Repository and installed-package inspection plus official documentation; no live deployment exercised.

## Existing adapter and packages

**Decision**: Keep C# 14/.NET 10, Anthropic 12.29.0, Foundry 0.7.0, and Microsoft.Extensions.AI 10.7.0.

**Rationale**: Installed types include hosted execution tools, execution content, file references, container fields, raw JSON unions, and SDK file services. The existing converters currently drop server execution blocks. `GetService` already exposes the SDK client.

**Alternatives considered**: A new provider, agent platform, custom execution service, or package upgrade is unnecessary for the inspected requirements.

## Initial tool profile

**Decision**: Initially advertise `code_execution_20250825`.

**Rationale**: It covers the specified shell/file workflows and exists in the pinned package. Container continuity does not promise persistent Python variables. Later profiles require explicit compatibility work. [Official versions](https://platform.claude.com/docs/en/agents-and-tools/tool-use/code-execution-tool#tool-versions).

**Alternatives considered**: Newer versions add behavior not required by these acceptance cases; model-name guessing is unreliable.

## Hosting and project connection

**Decision**: Keep existing resource/direct connections; accept an explicit hosting hint and preserve provider errors when eligibility is unknown.

**Rationale**: Foundry Hosted on Azure currently lacks execution and Files, unlike eligible Hosted on Anthropic deployments. A mandatory project client does not supply absent capability. [Official Foundry comparison](https://platform.claude.com/docs/en/build-with-claude/claude-in-microsoft-foundry).

**Alternatives considered**: Project provisioning and cross-provider fallback violate scope; endpoint names do not reliably identify hosting.

## Durable replay

**Decision**: Preserve cloned provider JSON in serializable versioned metadata, alongside normalized content.

**Rationale**: Installed-assembly probes show unknown union discriminators preserve `ContentBlock.Json` while the typed value is null, and `ContentBlockParam(JsonElement)` round-trips it. `AIContent.RawRepresentation` is JSON-ignored; base `AIContent` can represent unknown blocks without a new public hierarchy. Test real serialization round-trips, not only in-memory objects.

**Alternatives considered**: RawRepresentation alone fails persistence; projection-only reconstruction loses signatures, unknown fields, and operation-specific content.

## Streaming

**Decision**: Indexed block assembly, metadata-only partial progress, one typed completed call/result, and an ordered response-level replay collection.

**Rationale**: Direct probes of installed 10.7 show adjacent same-ID code-interpreter calls can coalesce, including nested text. In particular, stdout/stderr represented as adjacent TextContent merge and lose channel separation. Message-ID-bearing update metadata aggregates onto the assistant message, not the response. Therefore use a canonical assistant-message replay collection, one completed typed call, and separate text/plain DataContent output channels with canonical channel strings in result metadata. The current adapter also repeats accumulated text at EOF. SDK message-delta events carry container metadata. Results arrive as complete blocks; incremental stdout is not promised. [Provider streaming](https://platform.claude.com/docs/en/agents-and-tools/tool-use/code-execution-tool#streaming); [official aggregation source](https://github.com/dotnet/extensions/blob/main/src/Libraries/Microsoft.Extensions.AI.Abstractions/ChatCompletion/ChatResponseExtensions.cs).

**Alternatives considered**: Depending on adjacent-call coalescing makes fragmented-call behavior sensitive to intervening blocks. Hosted actions represented as application functions could invoke code locally. Per-content metadata alone can be lost during text coalescing. An earlier inference from source inspection about absent code-call coalescing was rejected after the installed-version execution probe. Text/plain DataContent also needs distinct Name values (`stdout`, `stderr`) to avoid same-name text-data coalescing; canonical channel strings remain authoritative. Unknown complete blocks can be preserved, but unknown delta semantics cannot safely be guessed into complete replay blocks.

## Continuation

**Decision**: Return explicit container metadata, preserve `pause_turn`, and require caller-driven continuation with message history.

**Rationale**: Container state is not generic provider-managed conversation history. Expiry metadata is informational; actual rejection comes from the provider. A hosted result may arrive in a subsequent response when ordinary functions are involved. [Continuation behavior](https://platform.claude.com/docs/en/agents-and-tools/tool-use/code-execution-tool).

**Alternatives considered**: Shared last-container state mixes conversations; automatic loops/restarts create unbounded or repeated work.

## Retry control

**Decision**: Execution requests bypass adapter retries and use `IMessageService.WithOptions(static o => o with { MaxRetries = 0 })`.

**Rationale**: Local synthetic probes verified distinct scoped services, unchanged original retry settings, and retained Foundry endpoint/client type. The interface works for the message-service-only constructor. Installed documentation says zero disables SDK retries. Caller-installed HTTP retry handlers remain outside this guarantee. Current official sources corroborate service cloning: [MessageService](https://github.com/anthropics/anthropic-sdk-csharp/blob/main/src/Anthropic/Services/MessageService.cs), [Foundry client](https://github.com/anthropics/anthropic-sdk-csharp/blob/main/src/Anthropic.Foundry/AnthropicFoundryClient.cs).

**Alternatives considered**: Counting only converted updates misses raw execution events. Suppressing only adapter retries leaves SDK retries active. Mutating the shared client changes unrelated requests.

## Files and authorization

**Decision**: Explicit `Beta.Files.Upload`, `RetrieveMetadata`, and `Download`; uploaded references map to `container_upload`.

**Rationale**: Installed signatures and synthetic parameter-builder probes verify the required operations and automatic Files beta header. Files are workspace-scoped; applications authorize file IDs for users. Downloads apply to generated files, not uploaded originals. [Files API](https://platform.claude.com/docs/en/build-with-claude/files). Generated-image downloads can include provider-added content credentials; preserve downloaded bytes. [Generated file retrieval](https://platform.claude.com/docs/en/agents-and-tools/tool-use/code-execution-tool#retrieve-generated-files).

**Alternatives considered**: A new file abstraction duplicates an exposed SDK service. Automatic byte upload/download hides network work and persistence.

## Evidence and remaining dependencies

Repository evidence: `Directory.Build.props`, `Directory.Packages.props`, `AnthropicChatClient.cs`, the five converters, and current tests. Installed package probes used synthetic data and no real credentials or network execution. They verified union constructors, properties, file methods, retry cloning, beta headers, and generic content serialization.

All design questions have decisions. Live deployment availability, credentials, and release evidence remain external dependencies; research is not production validation.
