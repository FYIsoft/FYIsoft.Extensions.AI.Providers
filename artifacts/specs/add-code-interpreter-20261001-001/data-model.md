# Data Model: Hosted Execution

**Status**: Planned contract, not implemented. No database or new server endpoint.

## Entities and relationships

| Entity | Representation and fields | Relationships / validation |
| --- | --- | --- |
| Request context | Internal immutable values: execution-bearing flag, provider name, connection scope, hosting hint, optional container ID | One per request; no stored conversation state. Declaration enables new execution; history/continuation also suppress retries. |
| Execution call | `CodeInterpreterToolCallContent`: `CallId`, `Inputs`; metadata operation name, full input JSON, block envelope | Call ID is the provider ID, never synthesized. Block index orders content. Shell/editor input must retain its original meaning. |
| Execution result | `CodeInterpreterToolResultContent`: `CallId`, `Outputs`; nullable return code, error code, outcome, canonical stdout/stderr strings, envelope | References `tool_use_id`; may arrive in a later response. Outputs include separately labeled UTF-8 text/plain DataContent channels and hosted files. Missing return code is not zero. |
| File reference | `HostedFileContent`: file ID, available name/media type, connection scope | Belongs to configured provider connection; ownership authorization remains in the application. Metadata lookup and download are explicit. |
| Provider block envelope | Version, provider, scope, message ID, block index, role, completion marker, original JSON object | Defined by [JSON schema](contracts/provider-envelope.schema.json). One envelope per complete top-level block. Unknown discriminators and extra fields are retained. |
| Response replay collection | Ordered array of envelopes on assistant-message metadata; optional response-level copy | Canonical full message representation, including text and signatures. Terminal updates carry MessageId so aggregation retains the collection on that message. Unique increasing indices; one message ID/provider/scope/role. Never replay projected nested outputs twice. |
| Stream block state | Internal index, kind, call ID, start payload, fragment buffer, emitted flag, terminal flag | One per block; request-local. Complete JSON must parse before typed call emission. No duplicate typed call/result. |
| Execution progress | Update metadata: message ID, index, optional call ID, operation, raw partial input, state | Inspectable incomplete progress; never valid replay input. No promised stdout deltas. |
| Container continuation | Container ID, provider/scope, returned expiry information | Explicitly resupplied by caller together with history. Expiry timestamp is informational, not a local hard rejection rule. |
| Response state | Provider stop reason plus `complete`, `paused`, `incomplete`, or `failed` | Separate from execution outcome. A completed response may contain an execution failure. Cancellation remains an exception. |

## State transitions

Execution input: `not_seen -> receiving -> complete`. On malformed JSON, EOF, cancellation, or protocol inconsistency before completion, it becomes incomplete/failed; it never produces a complete executable input. A complete call need not have a same-response result.

Execution result: `not_seen -> received`, with outcome `succeeded`, `failed`, or `unknown` derived only from supplied fields. Zero return code indicates command success; nonzero return code or explicit provider error indicates failure. Missing fields or an unrecognized result cannot be promoted to success.

Response transport: `receiving -> complete` only after provider `message_stop`; a `pause_turn` reason yields `paused`, length truncation yields `incomplete`, and unexpected EOF yields `incomplete`. Transport errors throw; already delivered progress remains incomplete to consumers. Cancellation throws `OperationCanceledException` and does not emit a successful terminal update.

Continuation: `absent -> returned -> explicitly_resubmitted`. Provider refusal/expiration is surfaced; restart requires caller action. No global transition causes another conversation to inherit state.

## Validation rules

1. Reject duplicate hosted declarations and unsupported hosted input types before network work. None tool selection cannot be overridden by continuation or input processing.
2. For tool-level files, require a user message, preserve caller history, and deduplicate only identical references within the target message. Reject explicit foreign scopes.
3. Use the client's opaque connection scope by default. Persisted history across client instances requires an explicitly supplied stable scope bound by the application to the same authorized connection. Provider name must also match.
4. Validate envelope version, scope, message/role/index consistency, complete marker, and object-shaped provider block. Validate discriminator presence without restricting unknown future values.
5. At message replay, a complete response-level collection is authoritative; otherwise accept complete per-content envelopes in content order. Reject mismatched/incomplete collections rather than silently reconstructing or dropping blocks.
6. Read metadata supplied directly as CLR values or deserialized as `JsonElement`. Clone JSON to avoid disposed-document references. Keep `RawRepresentation` only as an optional in-process view.
7. Set output DataContent.Name to its channel name; both channel metadata and canonical result strings are retained. Never rely on nested output object identity surviving generic aggregation.
8. Retain uninterpreted future streaming events for inspection, but mark affected blocks nonreplayable/incomplete if their input cannot be reconstructed. Unknown complete blocks remain replayable through the envelope.
9. Default diagnostics cannot include serialized envelopes, partial JSON, code, outputs, file contents, or credential material.

## Lifetime and ownership

The adapter owns request-local buffers and the generated opaque client scope only. The caller owns history, authorization, explicit continuation decisions, file streams, and persisted envelopes. The provider owns remote workspace/file lifetime. No automatic cleanup or infrastructure lifecycle policy is introduced.
