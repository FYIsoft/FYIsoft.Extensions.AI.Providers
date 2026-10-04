# Contract: Anthropic Hosted Execution through IChatClient

**Version**: v1, implemented and verified offline and live on Hosted on Anthropic Foundry Sonnet 4.6 on 2026-10-04; see [current SDK status](../../../../docs/sdk-status.md) for the verified route and publication status.

## Public surface

Retain `IChatClient.GetResponseAsync`, `GetStreamingResponseAsync`, all current constructors, and `GetService`. Enable execution with one `HostedCodeInterpreterTool` in `ChatOptions.Tools`. Add a public `AnthropicCodeExecutionMetadata` constants class for the keys below; no new client interface or server endpoint.

| Key | Location | Type / meaning |
| --- | --- | --- |
| `anthropic_hosting` | Request | Optional string: `direct`, `foundry-anthropic`, `foundry-azure`; omitted means unknown |
| `anthropic_provider_scope` | Request, response, message, relevant content | Nonempty opaque string bound to the authorized provider connection; default is per-client scope |
| `anthropic_container_id` | Explicit request, response/update | Nonempty container ID; request requires the matching provider scope |
| `anthropic_container_expires_at` | Response/update | Provider expiry value encoded as ISO timestamp when supplied; informational |
| `anthropic_stop_reason` | Response/update and persisted assistant message | Original provider reason; never silently normalize away `pause_turn` |
| `anthropic_execution_state` | Response/update and persisted assistant message | `complete`, `paused`, `incomplete`, `failed`; absent during ordinary chat |
| `anthropic_execution_progress` | Stream update | JSON object: message ID, block index, optional call ID/operation, partial JSON, state |
| `anthropic_operation` | Execution call | Original provider tool name |
| `anthropic_input` | Execution call | Complete provider input JSON object |
| `anthropic_outcome` | Execution result | `succeeded`, `failed`, `unknown` |
| `anthropic_return_code` | Execution result | Integer when supplied, otherwise absent |
| `anthropic_error_code` | Execution result | Provider error string when supplied |
| `anthropic_output_channel` | Nested text/plain DataContent | `stdout` or `stderr`; also set DataContent.Name to that value to prevent cross-channel coalescing |
| `anthropic_stdout`, `anthropic_stderr` | Execution result | Canonical strings when supplied; empty string remains distinct from absent |
| `anthropic_raw_content_block` | Top-level content | Complete [provider envelope](provider-envelope.schema.json) |
| `anthropic_replay_blocks` | Response, terminal update, persisted assistant message | Ordered array of complete provider envelopes for the whole message |

Treat metadata values as either their documented CLR type or a deserialized `JsonElement` of that shape. Invalid types fail with parameter/key context, without echoing sensitive values. Metadata keys are not automatically forwarded to the provider. `ChatOptions.ConversationId` is not a container ID.

## Requests and tool selection

Map hosted execution to `CodeExecutionTool20250825`. Existing function conversion/selection remains intact. None prohibits invocation, Auto allows model choice, Required requires a tool, and a named ordinary function remains that named function. Provider rejection of an unsupported combination is preserved. Hosted execution is never converted to an `AIFunction` or `FunctionCallContent`.

Accept `HostedFileContent` in the hosted tool's Inputs or directly in user-message content for execution. Require nonempty file IDs and an appropriate scope. Explicit user references with no scope are bound to the current request only when the caller supplies `anthropic_provider_scope`, making the authorization context explicit. References with a different scope are rejected. Inputs of other kinds, duplicate declarations, missing target user messages, unsupported hosting, and malformed continuation metadata fail locally. No raw byte/URI fetching or implicit file upload.

Continuation is explicit: resubmit the returned container ID and scope plus complete history. Replay does not automatically enable a tool or resume a paused turn. A new container starts only through a caller decision to omit old continuation state. Do not infer hard expiry from the reported timestamp.

## Projection and correlation

| Provider block | Standard projection |
| --- | --- |
| Recognized execution `server_tool_use` | `CodeInterpreterToolCallContent(id)` with full input metadata and operation-appropriate Inputs |
| Python/shell/editor execution result | `CodeInterpreterToolResultContent(tool_use_id)` with labeled output, outcome fields and nested file references |
| Generated file inside a result | Nested `HostedFileContent(file_id)`; optional name/media type only if supplied |
| Ordinary `tool_use` | Existing `FunctionCallContent` behavior |
| `container_upload` | `HostedFileContent` plus preserved block envelope |
| Unknown block or unknown server-tool operation | Base `AIContent` with raw envelope; no invented execution outcome |

Use exact provider IDs. Preserve result association even when the matching call is in earlier history. Keep original block order. A result's empty stderr/stdout is distinct from an absent field. Output channels use UTF-8 text/plain DataContent with distinct Name values; canonical stdout/stderr strings also remain on result metadata. Aggregation can mutate nested content collections, so raw replay JSON must be independently cloned. Preserve operation-specific extra fields through raw JSON even if the generic projection has no equivalent.

## Streaming and durable history

Partial input is metadata-only progress. Emit one complete typed call on block stop and one result for a complete result block. Stable response/message IDs and provider block indices correlate events. Progress is incomplete; no consumer should execute it locally. Malformed/truncated JSON is never converted to empty success.

For both response modes, return a canonical ordered `anthropic_replay_blocks` collection on the assistant message's AdditionalProperties. Streaming sends it in terminal metadata with the same MessageId; installed generic aggregation routes such metadata to the message. Put container, scope, stop reason, and execution state on the same message. Response-level copies are optional. Unexpected EOF supplies completed blocks with incomplete state; transport exceptions leave emitted progress available without manufacturing completion.

For persistence, append the returned assistant message with its canonical replay collection, scope, execution state, stop reason, and container metadata intact. Read message-level values first, with optional response-level copies as fallback. Serialized history can then be supplied to ordinary `GetResponseAsync` without a custom transport or raw SDK objects.

When a message has a replay collection, it is authoritative. Do not edit display projections and expect replay to change. To alter a message, prefer a fresh caller-owned replacement; otherwise remove/rebuild BOTH its message replay collection and affected per-content envelopes. Removing only one can restore stale content through the other. Preserve provider-required signatures. Validate same provider/scope, version, role, unique increasing indices, complete blocks, and complete/paused message state. Reject incomplete history unless the caller explicitly constructs a valid replacement. The full collection takes precedence over per-content envelopes and is replayed exactly once.

Unknown complete block types are inspectable and replayable. Unknown streaming delta semantics are preserved as raw event content for inspection; without a known reconstruction rule the affected block is marked incomplete/nonreplayable. Do not fabricate a completed input from unknown deltas. This forward-compatibility limit must be documented and tested.

`pause_turn` yields paused state and an unambiguous custom finish reason `new ChatFinishReason("pause_turn")`; it is not an application function call. Length truncation yields `ChatFinishReason.Length` and incomplete state. EOF without `message_stop` yields incomplete state. Cancellation throws. Execution success and response completion are independent.

## Files

Upload explicitly with the existing SDK, retain its returned ID, and attach the reference. Retrieve generated-file metadata and bytes using `IAnthropicClient.Beta.Files`; those SDK calls supply their default Files beta header. Message-service-only construction does not supply a file client, so callers use a separately held authorized SDK connection.

File names/media types are optional; no extra request is made just to fill them. Download response and stream lifetime belongs to the caller. Download the original provider response bytes without rewriting. No automatic download, file persistence, deletion, or cross-provider ID reuse. Scope metadata is not an authorization boundary; caller access policies remain authoritative.

## Errors, retries, and supported scope

Local validation uses argument/not-supported errors with safe context. Provider errors preserve their SDK identity/status and useful reason for the caller. Do not wrap all errors into a capability error. Execution-bearing requests disable adapter retries and SDK retries with a scoped message-service view. Network ambiguity is exposed; exactly-once execution is not promised, especially with caller-added retry middleware.

Default logs never serialize content or exception bodies. Generated messages and envelopes are returned content, not diagnostic logs. Existing application logging policies may require their own configuration.

Published support names the actual hosting arrangement, model/tool profile, package versions, and live smoke date. Known unsupported hosting fails clearly. Unspecified hosting is checked by the provider, without silently dropping the tool or switching providers. Newer execution profiles, agent platforms, and project provisioning are outside v1.
