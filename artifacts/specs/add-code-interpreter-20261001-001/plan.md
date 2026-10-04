# Implementation Plan: Opt-in Hosted Code Interpreter

**Feature**: `add-code-interpreter-20261001-001` | **Date**: 2026-10-01 | **Spec**: [spec.md](spec.md)
**Input**: `C:/dev/SuiteFYI/AISDK/artifacts/specs/add-code-interpreter-20261001-001/spec.md`
**Branch**: `spec/add-code-interpreter`
**Status**: Implementation, offline verification and live smoke tests on the existing Hosted on Anthropic Foundry Sonnet 4.6 deployment completed 2026-10-04. Publication remains pending; direct Anthropic is unverified. See [current SDK status](../../../docs/sdk-status.md). The design below records the original 2026-10-01 plan.

## Summary

Extend the existing Anthropic adapter to accept `HostedCodeInterpreterTool`, expose standard execution call/result content, preserve provider JSON for replay, and support explicit container continuation. Reuse the existing SDK for file operations. Eligible deployments require no additional Foundry project client.

The main work is conversion and lifecycle correctness: indexed streaming, lossless history, and execution-aware retry behavior. Keep the prior tool-selection fix and preserve ordinary chat behavior. No new execution service, provider, or background worker is required.

## Technical Context

**Language/Version**: C# 14, .NET 10, as configured in `Directory.Build.props`.
**Primary Dependencies**: Microsoft.Extensions.AI / Abstractions 10.7.0; Anthropic 12.29.0; Anthropic.Foundry 0.7.0; existing logging/DI abstractions 10.0.9.
**Storage**: Caller-owned history and downloaded files; provider-owned remote files and execution environments. No database or library-owned file store.
**Testing**: xUnit 2.9.2, FluentAssertions 6.12.2, Microsoft.NET.Test.Sdk 17.12.0; synthetic JSON/SSE fixtures, fake HTTP transport, separately enabled live smoke checks.
**Target Platform**: Existing .NET 10 consumers; direct Claude and eligible Foundry deployments.
**Project Type**: Existing library in a multi-project solution.
**Performance Goals**: One pass over blocks and event fragments; no repeated full-history copies per delta; no automatic file I/O. Buffer only content required to assemble and replay the current response. No provider latency commitment.
**Constraints**: Explicit opt-in; no local execution, automatic provider fallback, auto continuation, shared conversation state, or additional project connection. Preserve cancellation and exclude sensitive content from default diagnostics.
**Scale/Scope**: One adapter, five existing converters, two internal helpers, metadata constants, focused tests and documentation. No package upgrade currently indicated by inspected installed APIs.

## Constitution Check

No adopted constitution exists at `.claude/memory/spec/constitution.md` or `.ai-dlc/resources/memory/spec/constitution.md`. The SixSevenAI distribution contains an unfilled template, not a project policy. No constitution was invented or ratified. Gates below derive from the accepted specification and repository settings.

| Gate | Before research | After design | Evidence |
| --- | --- | --- | --- |
| Retain existing architecture and dependencies | Pass | Pass | Installed SDK supports required types |
| Opt-in and explicit continuation | Pass | Pass | Request contract and caller-owned state |
| No mandatory project or provider fallback | Pass | Pass | Existing resource connection retained |
| Backward-compatible ordinary chat | Pass | Pass | Execution-disabled regression matrix |
| Preserve content without default logging | Pass | Pass | Serializable envelopes and diagnostic checks |
| Avoid untested support claims | Pass | Pass | Deployment-specific release gates |

Workflow adaptation: the source command requests REST/GraphQL endpoints. This is a .NET library, so its [contracts](contracts/chat-client.md) describe `IChatClient`, SDK file operations, and a JSON replay schema. Adding a web service would violate scope. No unjustified gate violations or unresolved design questions remain.

## Project Structure

### Documentation (this feature)

Paths resolve beneath `C:/dev/SuiteFYI/AISDK/`.

```text
artifacts/specs/add-code-interpreter-20261001-001/
  spec.md
  plan.md
  research.md
  data-model.md
  quickstart.md
  contracts/chat-client.md
  contracts/provider-envelope.schema.json
  checklists/requirements.md
  planning-validation.md
reports/code-interpreter-plan.html
docs/runbooks/sixsevenai-spec-plan.md
```

`tasks.md` belongs to the next `spec:tasks` phase and is not created here.

### Source Code File Structure

Planned runtime changes, not files implemented by this command:

```text
src/FYIsoft.Extensions.AI.Anthropic/
  AnthropicChatClient.cs                       request context, retry, response metadata
  AnthropicCodeExecutionMetadata.cs            new public metadata constants
  Converters/
    AnthropicToolConverter.cs                  hosted tool declaration
    AnthropicOptionsConverter.cs               container and request validation
    AnthropicContentConverter.cs               execution/file projections and replay
    AnthropicMessageConverter.cs               ordered history and response metadata
    AnthropicStreamingConverter.cs             indexed assembly and terminal states
    AnthropicCodeExecutionConverter.cs         new shared projections
    AnthropicContentEnvelope.cs                new raw replay validation/helper
  README.md                                   usage and tested support matrix
tests/FYIsoft.Extensions.AI.Anthropic.Tests/
  AnthropicCodeExecutionRequestTests.cs
  AnthropicCodeExecutionContentTests.cs
  AnthropicCodeExecutionStreamingTests.cs
  AnthropicCodeExecutionReplayTests.cs
  AnthropicCodeExecutionLifecycleTests.cs
  AnthropicCodeExecutionFilesTests.cs
  AnthropicCodeExecutionLiveTests.cs
  FakeAnthropicHttpHandler.cs
  Fixtures/CodeExecution/*.json
  Fixtures/CodeExecution/*.sse
  FYIsoft.Extensions.AI.Anthropic.Tests.csproj   include fixture files if needed
  AnthropicOptionsConverterToolModeTests.cs     retain existing regression coverage
  AnthropicStreamingConverterUsageTests.cs      aggregation regression coverage
```

**Structure Decision**: Share internal conversion and replay helpers between streamed/completed paths. Keep existing constructors and registrations. Public additions are metadata constants; content and file services reuse existing types.

## Tech Stack and Dependencies

Keep centrally pinned versions. Installed assemblies expose the tool/result unions, raw JSON constructors, file operations, and container fields needed by this design. Initial tool declaration: `code_execution_20250825`. This covers calculation and file workflows, but does not promise persistent Python variable bindings or programmatic invocation of application functions. Later tool versions are explicit compatibility work, not inferred from model aliases.

`HostedCodeInterpreterTool.Inputs` initially accepts uploaded `HostedFileContent`; other input kinds fail clearly. Upload is separate. The installed `Beta.Files` parameter builders supply their default Files beta header automatically; no manual header is needed in examples.

## Architecture Overview

### Request flow and Integration Points

1. Snapshot messages/options without mutation. Exactly one `HostedCodeInterpreterTool` enables the feature; duplicate declarations fail.
2. Create request-local context for hosting hint, provider scope, and optional container. Never store a last conversation/container on the client.
3. Normalize tool input files onto the last user message, preserving order and deduplicating identical file IDs there. Reject tool inputs when no user message exists. Direct references in older messages retain their positions.
4. Validate scopes, supported input kinds, hosting, and tool selection. Preserve Auto/None/Required/named-function semantics. None forbids invocation even with a hosted declaration; inputs/continuation do not override selection.
5. Map the hosted tool with installed `CodeExecutionTool20250825`. Set `MessageCreateParams.Container` only from explicit caller state. Container state does not replace history or belong in `ChatOptions.ConversationId`.
6. Call the existing message service with execution-aware retry settings, then project ordered content and metadata.

### Completed content and replay

Recognized hosted calls become `CodeInterpreterToolCallContent`; results become `CodeInterpreterToolResultContent`; generated files appear as nested `HostedFileContent`. Preserve exact operation name and full input JSON. Distinguish shell/Python/editor input; never relabel every command as Python. Preserve empty stdout/stderr, optional return code, provider error code, and editor-specific fields.

Retain cloned provider JSON in a versioned serializable envelope on each top-level item of an execution response, including text, function calls, thinking/signatures, and unknown blocks. Unknown blocks use base `AIContent`. `RawRepresentation` remains inspection-only because serialization ignores it. Replay validates completeness, provider/scope, role, order, and schema version, then prefers original JSON over display projections. Do not replay nested outputs separately. Caller-created execution history without a valid envelope fails rather than silently disappearing.

### Streaming and aggregation

Maintain per-message, per-index block state. Emit partial execution JSON as metadata-only progress, and exactly one typed complete call at block stop. Emit each complete result once. Capture container data from start and message delta. A hosted call can await its result in another response; do not equate that with a truncated block.

Text streams normally without whole-text re-emission at EOF. Every completed block also enters an ordered replay collection. Emit that complete collection in a terminal update with the assistant MessageId so `ToChatResponse()` places it on the assistant message's AdditionalProperties. Provide the same canonical message-level metadata on nonstreamed responses; response-level copies are optional convenience only. This retains durable history even when text aggregation discards per-content metadata. Complete per-content envelopes remain useful for direct content replay. Represent stdout/stderr as separate UTF-8 text/plain DataContent plus canonical result metadata strings: adjacent TextContent outputs otherwise coalesce and lose channel identity in the installed aggregator.

Set projected output DataContent.Name to `stdout` or `stderr` as well as channel metadata; same-media-type unnamed text data can also coalesce. Keep canonical channel strings on result metadata and cloned raw JSON independent of mutable nested output collections. Only `message_stop` completes transport; `pause_turn` stays paused, and EOF without stop stays incomplete. Cancellation propagates without synthesized success. Malformed or unfinished execution JSON never becomes an empty valid input. Unknown complete block shapes remain replayable; unrecognized delta semantics are retained as raw diagnostic content for inspection and make the affected block nonreplayable/incomplete rather than guessed into a request. Raw events are returned content, never default logs.

### Retry, errors, diagnostics

For execution-bearing requests, use `_messageService.WithOptions(static o => o with { MaxRetries = 0 })` and bypass adapter retries. This installed interface supports both full-client and message-service-only constructors and leaves the original service unchanged. Execution-bearing includes continuation/history capable of resuming execution, even if selection forbids new calls. Ordinary requests retain their retry policy.

Request-counting tests must verify this at the HTTP boundary. Caller-installed retry middleware is outside the adapter guarantee. Network failure can leave outcome unknown; never claim exactly-once execution. Default logs include safe category/status identifiers without exception bodies, code, output, or file content. Preserve original provider failures to callers without automatically logging them.

### Files, continuation, isolation

Reuse `GetService<IAnthropicClient>()` and its `Beta.Files` upload/metadata/download operations. A message-service-only consumer supplies a separately held authorized files service. The caller disposes download response/stream objects. No automatic download, temporary persistence, deletion, or file-client abstraction is added.

Default provider scope is an immutable per-client opaque identifier, not conversation state. Callers can supply a stable nonsecret connection scope for persistence across client instances and must bind it to the same authorized connection. Returned envelopes/files carry provider name and scope; explicit continuation carries its container ID and scope. Mismatch fails locally. Scope is an accidental-mixing guard, not user authorization; users and tenants remain application-controlled.

Reject a caller-declared `foundry-azure` hosting arrangement locally. With unspecified hosting, preserve provider rejection instead of guessing from model IDs or endpoint names. Never silently remove the requested tool, switch providers, or create a project.

## Phase 0 — Research Outcome

[Research](research.md) resolves dependency, conversion, replay, streaming, file, hosting, and retry choices. Local installed-package probes establish feasibility. Deployment availability is handled through release evidence, not assumed from documentation.

## Phase 1 — Design Artifacts

- [Data model](data-model.md): entities, validation, and state transitions.
- [Chat client contract](contracts/chat-client.md): supported requests, metadata, mappings, errors.
- [Replay envelope schema](contracts/provider-envelope.schema.json): durable JSON shape.
- [Quickstart](quickstart.md): proposed usage and validation, explicitly not implemented yet.

Post-design gates pass as recorded in Constitution Check.

## Phase 2 — Implementation Sequence and Validation

1. Add metadata/envelope definitions, synthetic fixtures, request validation, hosted declarations, and file input mapping.
2. Implement completed projections and durable replay, including unknown fields, signatures, mixed function results, and serialization.
3. Implement indexed streaming, metadata progress, replay collections, and terminal state handling; prove aggregation parity.
4. Wire continuation, both-layer retry suppression, safe diagnostics, and SDK file examples; verify isolation and failure behavior.
5. Run the existing solution regressions; execute opted-in live smoke tests per advertised route; publish support guidance with evidence before an additive release.

| Acceptance group | Required evidence | Spec coverage |
| --- | --- | --- |
| Requests | Disabled/Auto/None/Required/named functions, mixed tools, duplicate declaration, unsupported input | FR-001, 004, 013–016; SC-005–006 |
| Completed content | Known calculations, shell/editor/legacy Python, errors, empty output, unknown fields, multiple calls, order | FR-002–003, 018; SC-001 |
| Streaming | Paired JSON/SSE, split escapes, already-present input, invalid input, indices, EOF, cancellation, generic aggregation | FR-005–006; SC-002 |
| Replay/continuation | Persisted round-trip, signatures, two interleaved conversations, scope mismatch, pause, later result, expiration | FR-010–012, 018; SC-004 |
| Files | Uploaded CSV, chart retrieval, file failure categories, no automatic I/O | FR-007–009; SC-003, 005–006 |
| Retry/diagnostics | HTTP counts under 5xx/IO/progress; sensitive marker strings absent from logs | FR-006, 014–017; SC-005–007 |
| Release | Live evidence for direct and eligible Foundry routes; ineligible route rejection; versions recorded | FR-019; SC-003, 005 |

Planned checks: `dotnet build FYIsoft.Extensions.AI.Providers.slnx` and `dotnet test FYIsoft.Extensions.AI.Providers.slnx` from `C:/dev/SuiteFYI/AISDK/`. Fixtures need no credentials. Live tests run only when explicitly enabled and configured. No runtime tests or live deployments are reported as validated by this documentation-only phase.

## Risks and Release Boundaries

SDK union serialization and generic aggregation require regression tests. Provider/model compatibility must be rechecked at release. Envelopes contain sensitive history and share the caller's storage policy. Network failure can leave remote execution outcome unknown. Deployment support is advertised only after live calculation, file analysis/download, and continuation checks succeed on the actual route. This plan does not publish packages, provision infrastructure, or implement code interpreter.
