# Feature Specification: Opt-in Hosted Code Interpreter

**Feature**: `add-code-interpreter-20261001-001`
**Created**: 2026-09-30 (session identifier uses UTC)
**Status**: Implemented and verified offline and live on Hosted on Anthropic Foundry Sonnet 4.6 on 2026-10-04. Publication remains pending; direct Anthropic is unverified. See [current SDK status](../../../docs/sdk-status.md).
**Input**: User description: Support code interpreter content in the existing Anthropic provider, including execution, results, streaming, files, and conversation continuation. Establish whether a Foundry project connection is required. Produce the specification before implementing this feature.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Run calculations and inspect results (Priority: P1)

As an application developer, I can explicitly enable hosted code execution so users receive calculated answers and the application can distinguish executed code from ordinary assistant text.

**Why this priority**: Reliable execution content is the core capability missing from the current provider.

**Independent Test**: Enable execution for one supported conversation and request a calculation with a known result; inspect the execution and answer. Repeat with execution disabled.

**Acceptance Scenarios**:

1. **Given** an eligible deployment and explicit opt-in, **When** the assistant executes a calculation, **Then** the application receives the execution identity, supplied code or commands, corresponding output, and final answer in their original order.
2. **Given** an execution that fails, **When** its result arrives, **Then** available error output and completion status remain identifiable as a failure rather than a successful calculation.
3. **Given** execution is disabled, **When** an ordinary conversation runs, **Then** no hosted execution capability is requested and existing text and application function behavior remains unchanged.
4. **Given** hosted execution and application functions are both enabled, **When** either kind is requested, **Then** the application can distinguish them and never executes provider-hosted commands locally.

### User Story 2 - Observe execution as it happens (Priority: P1)

As a chat application developer, I can show execution progress and distinguish an unfinished run from a completed result.

**Why this priority**: The existing provider supports streamed conversations; code execution must work within that experience.

**Independent Test**: Replay representative execution progress with multiple chunks and compare the assembled content with the equivalent completed response.

**Acceptance Scenarios**:

1. **Given** a streamed execution, **When** progress arrives, **Then** every available execution fragment is associated with its execution and exposed in order without duplication.
2. **Given** equivalent streamed and completed responses, **When** the application assembles the stream, **Then** their execution identities, inputs, results, and file references agree.
3. **Given** an interrupted or cancelled stream, **When** delivery stops, **Then** incomplete content is distinguishable from success, cancellation is honored, and execution is not silently replayed.

### User Story 3 - Analyze a file and retrieve generated output (Priority: P1)

As a user of an integrating application, I can provide a data file, ask for analysis, and obtain the resulting chart or transformed file.

**Why this priority**: File-based analysis is a primary reason to use code interpreter.

**Independent Test**: Use a synthetic sales file with known totals, request a summary and chart, and retrieve the generated file through the application's authorized provider connection.

**Acceptance Scenarios**:

1. **Given** a supported input file authorized for this conversation, **When** analysis is requested, **Then** the execution can access that file and return the expected totals.
2. **Given** a generated chart or document, **When** execution completes, **Then** the application receives a retrievable reference with available name and media information and can explicitly retrieve the original bytes.
3. **Given** an expired, unauthorized, incompatible, oversized, or missing file, **When** it is submitted or retrieved, **Then** the application receives an actionable failure and never receives an unrelated file.
4. **Given** a response containing file references, **When** it is received, **Then** the library does not automatically download files or persist their contents locally.

### User Story 4 - Continue analysis within one conversation (Priority: P2)

As an application developer, I can explicitly continue a previous execution environment without mixing work from different conversations.

**Why this priority**: Follow-up analysis needs continuity while shared application clients must remain safe to reuse.

**Independent Test**: Run two interleaved conversations using one client; continue one with its own state and verify the other remains independent.

**Acceptance Scenarios**:

1. **Given** returned continuation information and execution history, **When** the caller explicitly supplies them on a follow-up, **Then** the provider can continue the same environment and receive the prior execution content intact.
2. **Given** two conversations, **When** their requests interleave, **Then** the library never implicitly transfers continuation information, files, or history between them.
3. **Given** expired continuation information, **When** reuse is attempted, **Then** expiration is surfaced and any restart requires an explicit caller decision.
4. **Given** a provider pause awaiting continuation, **When** that response arrives, **Then** the application can distinguish the pause from completion and explicitly resume without an unbounded automatic continuation loop.

### User Story 5 - Understand deployment eligibility (Priority: P1)

As an application owner, I can determine whether my configured Anthropic deployment supports execution and receive useful feedback when it does not.

**Why this priority**: Model hosting choices differ; project configuration alone cannot create an unavailable capability.

**Independent Test**: Exercise eligible and ineligible deployment responses, including authentication failures, and verify the advertised support information against release evidence.

**Acceptance Scenarios**:

1. **Given** a compatible direct Claude or Foundry deployment, **When** execution is enabled, **Then** the library uses that deployment without introducing a mandatory Foundry project connection.
2. **Given** a known unsupported configuration, **When** execution is requested, **Then** it fails clearly; when eligibility cannot be determined beforehand, the provider's rejection is preserved with useful context.
3. **Given** unavailable capability, invalid credentials, or exhausted quota, **When** the request fails, **Then** the application can distinguish the reason without silent tool removal, provider switching, or infrastructure provisioning.
4. **Given** sensitive execution content, **When** the library reports status or failure, **Then** its default diagnostics omit code, file contents, credentials, and execution output while still identifying the operation and outcome.

### Edge Cases

- Multiple executions and ordinary function calls in one turn must retain distinct identities and original order.
- Empty output, nonzero exit status, and provider-reported errors must remain distinguishable.
- A future, unrecognized provider content form must remain available for inspection and conversation replay rather than disappearing silently.
- Truncated streamed input must not be represented as a complete executable request.
- Failure after partial output must retain delivered progress without falsely claiming completion or retrying execution automatically.
- File access and continuation references are provider-scoped; they must never be implicitly reused against another configured provider.
- A model may choose not to execute code even when enabled; the ordinary answer remains valid and must not invent execution events.

## Requirements *(mandatory)*

### Functional Requirements

- **FR-001**: Hosted execution MUST require explicit opt-in for the request or caller-configured conversation; default chat behavior MUST remain unchanged.
- **FR-002**: The system MUST expose each hosted execution's identity, available input, and associated result as distinguishable content, preserving original content order.
- **FR-003**: Execution results MUST preserve available standard output, error output, exit status, and provider-reported failures without manufacturing missing information.
- **FR-004**: Hosted execution MUST remain distinguishable from application functions and MUST NOT cause local execution of hosted commands.
- **FR-005**: Streaming MUST expose available execution progress with stable associations and produce completed content equivalent to the corresponding nonstreamed response.
- **FR-006**: Cancellation and interrupted delivery MUST preserve incomplete status and MUST NOT trigger silent execution replay after progress has been delivered.
- **FR-007**: Callers MUST be able to supply provider-supported input files through their authorized connection and associate those files with the intended conversation.
- **FR-008**: Generated files MUST expose provider-scoped references, available descriptive metadata, and an explicit authorized retrieval path that preserves file bytes.
- **FR-009**: Missing, inaccessible, expired, incompatible, and over-limit files MUST yield actionable failures; file download and local persistence MUST be explicit caller actions.
- **FR-010**: Callers MUST be able to receive and explicitly resupply conversation continuation information and execution history without losing provider-required content.
- **FR-011**: Reusing a client MUST NOT implicitly share execution environments, files, history, or continuation information between conversations.
- **FR-012**: Expired execution environments and provider pauses MUST be distinguishable from completion; restart and continuation MUST be caller-controlled.
- **FR-013**: Eligible direct Claude and Foundry deployments MUST work without an additional mandatory Foundry project connection introduced by this feature.
- **FR-014**: Known unsupported execution configurations MUST be rejected clearly; otherwise provider capability, authentication, quota, and permission failures MUST retain their distinguishing information.
- **FR-015**: The system MUST NOT silently drop requested execution capability, switch providers, or provision infrastructure to satisfy a request.
- **FR-016**: Existing text, image, application function selection, function results, cancellation, and streaming behavior MUST remain compatible when execution is disabled.
- **FR-017**: Default diagnostics MUST omit execution inputs, outputs, file contents, and credentials, while allowing callers to identify the operation and its success, failure, pause, or cancellation.
- **FR-018**: Previously unrecognized execution content MUST remain inspectable and replayable without falsely presenting it as a recognized successful result.
- **FR-019**: Published support guidance MUST distinguish eligible hosting arrangements, required provider permissions, and tested deployment coverage; each advertised execution route MUST have a successful live smoke test before release.

### Key Entities *(include if feature involves data)*

- **Execution request**: One provider-hosted action with its identity and available code or command input.
- **Execution result**: The associated output, status, errors, and generated file references.
- **Execution progress**: Ordered partial content associated with an execution, including whether it has completed.
- **Execution environment**: A provider-hosted workspace whose continuation is explicit and may expire.
- **Conversation state**: Caller-owned history and continuation information scoped to a conversation and provider.
- **File reference**: Provider-scoped identity and available descriptive information for an authorized input or generated output file.
- **Deployment capability**: The execution eligibility of the configured model and hosting arrangement.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: Across the acceptance corpus covering successful, failed, empty-output, multiple-execution, mixed-function, and unrecognized-content responses, 100% of supplied execution identities and available results remain accessible and correctly associated.
- **SC-002**: Every paired streamed/completed case yields equivalent completed execution content; every interruption and cancellation case reports incomplete work without duplicate execution or false success.
- **SC-003**: A user can complete all three reference tasks—known calculation, synthetic file analysis, and generated-chart retrieval—with correct results and usable output on every deployment advertised as supported.
- **SC-004**: Two interleaved conversations demonstrate zero implicit transfer of state or files; all explicit continuation, pause, and expiration scenarios produce their specified outcomes.
- **SC-005**: Every unsupported-capability, authentication, quota, permission, and file-failure scenario identifies the relevant failure category; zero scenarios silently switch providers or omit requested execution capability.
- **SC-006**: All existing provider regression checks pass with execution disabled, with zero additional hosted execution requests or automatic file downloads.
- **SC-007**: Inspection of default diagnostics for every reference task and failure case finds zero execution inputs, outputs, file contents, or credentials.

## Scope

This feature extends the existing Anthropic provider with native hosted code execution content, streaming, files, and explicit continuation. Support follows the configured deployment's actual capabilities. It does not build an agent platform, add an Azure OpenAI provider, introduce cross-provider fallback, or create a local execution service. The separate Anthropic tool-selection fix remains part of the compatibility baseline.

## Assumptions and Dependencies

- Applications retain responsibility for user and tenant authorization, data classification, retention, and the decision to send content to their configured provider. This library introduces no new user roles or data-access grants. Content is treated as potentially sensitive; acceptance examples use synthetic files.
- Callers provide an existing authorized provider connection. Provider-controlled regional availability, supported models, quotas, file limits, and execution lifetimes remain external dependencies.
- Live deployment access is required to substantiate release claims. None has been validated during specification; research alone is not release evidence.
- Execution duration depends on the workload and provider. This feature does not promise a fixed completion time or guarantee that an enabled model will choose execution.
- Detailed implementation choices and the final deployment test matrix belong to planning. Current research is recorded in [the HTML research report](../../../reports/code-interpreter-research.html).
