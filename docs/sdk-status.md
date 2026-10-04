# SDK completion status

Verification date: 2026-10-04. Branch: `spec/add-code-interpreter`.

Both SDKs are packaged and verified offline and against live providers. **The hosted-execution
blocker is resolved:** all four Anthropic live tests pass using the existing `claude-sonnet-4-6`
Foundry deployment (version 1, Hosted on Anthropic). Jev's four live tests also pass. The sample's
`claude-sonnet-5` deployment is version 2, Hosted on Azure, and cannot run hosted code execution.
No Azure resources were created or changed, and neither package has been published.

## Six provider features added in 0.6.0-preview

- Thinking: ChatOptions.Reasoning maps to adaptive thinking/effort or an explicit manual budget.
  Responses and streams expose TextReasoningContent, signatures, redacted blocks and durable replay.
- Citations: CitationAnnotation carries title, URL and snippet; original source locations remain JSON
  metadata. Streaming retains annotations through generic aggregation without duplicating text.
- Documents: base64 PDF, HTTPS PDF URL, and plain-text documents with citation/title/context options.
- Caching: automatic request caching and explicit content/system/tool breakpoints; cache-read,
  cache-creation and total input usage, including streamed snapshots.
- Web search: HostedWebSearchTool translates to web_search_20250305; typed calls, results and citations.
- MCP: HostedMcpServerTool translates server/toolset/authentication/allowlists plus the beta header;
  typed calls/results, error flags, and retry suppression for potential remote side effects.

All six features passed five live workflows on `claude-sonnet-5` (Foundry version 2) at 17:30 UTC.
The tests cover streamed reasoning plus serialized replay, PDF citations in completed/streaming
responses, a real cache creation/read pair, actual web search with answer citations, and a public
read-only Microsoft Learn MCP search restricted to `microsoft_docs_search`. No private documents or
customer data were sent. Evidence: `artifacts/live-test-results/provider-features-sonnet5/*.trx`.

MCP interactive approval modes and unsupported headers fail explicitly before sending. Thinking
Full is rejected because the provider only exposes summaries. Ordinary rich history is separate
from execution restrictions; incomplete rich history cannot be replayed. Direct Anthropic API
routing remains covered by synthetic HTTP tests, not live credentials.

The user deployed `claude-sonnet-5-5` during this work. An initial check at 17:31 UTC found it
Creating (HTTP 400 DeploymentError); Azure subsequently reported Succeeded, version 2. The earlier
404 record below is historical, not the current deployment state. After provisioning completed,
all five feature workflows passed on Sonnet 5.5 (19 seconds) and Opus 5.5 (26 seconds). The four
existing Anthropic workflows also passed again on Sonnet 4.6 (34 seconds), including actual code
execution and file/container continuation. The 0.6.0-preview nupkg restored and executed in a
separate package-only consumer, covering rich options, PDF input, thinking, citations and cache usage.

| New feature verification | Result | Evidence directory |
| --- | --- | --- |
| Sonnet 5, version 2 | 5 live workflows passed | `artifacts/live-test-results/provider-features-sonnet5` |
| Sonnet 5.5, version 2 | 5 live workflows passed after provisioning | `artifacts/live-test-results/provider-features-sonnet55-ready` |
| Opus 5.5, version 2 | 5 live workflows passed | `artifacts/live-test-results/provider-features-opus55` |
| Sonnet 4.6 existing chat/execution regression | 4 live workflows passed | `artifacts/live-test-results/execution-regression` |
| Release build | 0 warnings, 0 errors | `dotnet build ... -c Release -warnaserror` |
| Offline suite | 120 Anthropic + 42 Jev passed | `artifacts/test-results/provider-features` |
| Package consumer | Passed with 0.6.0-preview | `artifacts/package-smoke` |

Rerun the five new live workflows with the existing live-test environment variables configured:

```powershell
dotnet test tests/FYIsoft.Extensions.AI.Anthropic.Tests -c Release `
  --filter 'FullyQualifiedName~AnthropicProviderFeaturesLiveTests' `
  --logger trx --results-directory artifacts/live-test-results/provider-features
```

The MCP test calls only the public read-only `microsoft_docs_search` tool. Tests use a synthetic
single-page PDF and synthetic cache prefix. Package publication and Telli UI integration remain
separate release/consumer work; no package was published and no Telli files were changed.

## Where the projects stood

- Jev already contained its typed API client, IChatClient adapter, structured question conversion,
  retry/error handling and 42 passing offline tests. Four existing live tests were skipped in the baseline run.
- Anthropic contained ordinary chat/function/streaming support and 33 passing tests, including an
  uncommitted tool-selection fix. Hosted execution existed only as specification/design documents.
- Existing worktree changes and design artifacts were preserved.

## Work completed

Anthropic `0.6.0-preview` includes the previous hosted-execution work: it maps `HostedCodeInterpreterTool` to `code_execution_20250825`,
projects calls/results and generated files, normalizes uploaded file inputs, supports explicit
container continuation, and preserves complete provider blocks in serializable replay envelopes.
Scope/role/version/order/state validation rejects incompatible or incomplete history.

Streaming now assembles blocks by index, handles split JSON and thinking signatures, emits complete
calls once, keeps stdout/stderr separate through generic aggregation, and preserves unknown blocks.
Partial input, unknown deltas, truncated delivery, pause and cancellation are distinct from success.
Both retry layers are disabled for execution-bearing requests, including history/continuation
without a new tool declaration. Ordinary chat retries remain; default logs omit exception bodies.

Jev remains `0.1.0-preview`; its full offline and live suites pass. Both SDKs now require explicit
enablement plus credentials for live tests. Anthropic has four live workflows covering ordinary
chat/streaming, function selection/continuation, streaming calculation, and uploaded CSV/chart
download/serialized container continuation.

Provider READMEs include usage, replay rules, file operations and live-test commands. CI builds
Release with warnings as errors, runs offline tests, packs both SDKs and uploads TRX/package artifacts.

## Evidence

| Check | Result |
| --- | --- |
| Release solution build, warnings as errors | Pass: 0 warnings, 0 errors; both SDKs, tests and SupportDeskSample |
| Anthropic offline tests | 120 passed, including 21 new provider-feature tests |
| Jev offline tests | 42 passed |
| Total offline tests | 162 passed, 0 failed |
| Anthropic ordinary live tests | 2 passed: completed/streaming text and usage; named function call followed by streamed tool-result continuation |
| Anthropic hosted-execution live tests | 2 passed on Sonnet 4.6 version 1: executed calculation, CSV total, PNG download, serialized container continuation |
| Jev live tests | 4 passed: model listing, all question types, structured output, authentication failure |
| Combined SupportDeskSample live run | Pass: Jev triage, Claude `get_account` function and response, Jev review |
| NuGet packaging | Both packages created successfully |
| Package consumer smoke | Both local nupkgs restored into a separate console project and executed against synthetic HTTP transports |
| Whitespace validation | `git diff --check` passes |

Commands used from the repository root:

```powershell
dotnet build FYIsoft.Extensions.AI.Providers.slnx -c Release --no-restore -warnaserror
dotnet test FYIsoft.Extensions.AI.Providers.slnx -c Release --no-build --logger trx --results-directory artifacts/test-results
dotnet pack FYIsoft.Extensions.AI.Providers.slnx -c Release --no-build -o artifacts/packages
dotnet run --project artifacts/package-smoke/package-smoke.csproj -c Release
```

Local build evidence (ignored by Git):

- `artifacts/test-results/*.trx`: machine-readable results for both test assemblies.
- `artifacts/live-test-results/*.trx`: live pass/failure evidence; `support-desk-smoke.txt` records the synthetic sample workflow.
- `artifacts/live-test-results/sonnet-4-6/*.trx`: all four Anthropic live tests passed on the execution-capable deployment.
- `artifacts/packages/FYIsoft.Extensions.AI.Anthropic.0.6.0-preview.nupkg`.
- `artifacts/packages/FYIsoft.Extensions.AI.Jev.0.1.0-preview.nupkg`.
- `artifacts/package-smoke`: package-reference-only consumer source and executable; no project references.

## Acceptance coverage and remaining release work

| Acceptance area | Evidence | Remaining |
| --- | --- | --- |
| Requests, modes, functions, uploaded inputs | HTTP payload tests, local rejection, no caller mutation; ordinary and hosted functions live verified | None for the verified Foundry route |
| Execution content, outcomes, unknown fields, files | Calculation/result/error fixtures, raw envelope comparisons; live sum of squares equals 338350 | None for the verified Foundry route |
| Streaming and aggregation | Indexed interleaving, split escapes, signatures, malformed/unknown deltas, EOF, pause, cancellation; live ordinary and execution streaming | None for the verified Foundry route |
| Replay and isolation | Serialized replay, canonical/per-content history, mismatched scopes/roles/versions/order, independent containers; live persisted container continuation | None for the verified Foundry route |
| Files | Explicit SDK upload/metadata/download with byte equality; 403/404/429 status preservation; live CSV sum 60 and PNG signature verified | None for the verified Foundry route |
| Retry and errors | Counted HTTP requests with SDK retries configured; both constructors; failure before/after progress; live incompatible deployment rejection | None for the verified Foundry route |
| Logging | Sensitive-marker assertions on error/retry paths | Application-specific logging configuration is caller-owned |
| Packaging | Local restore and API execution from both nupkgs; live route evidence recorded | Commit/review, CI and preview publication |

## Live deployment record

On 2026-10-04 at approximately 05:14–05:16 UTC, the sample's existing .NET user-secret configuration
supplied a Foundry connection using `claude-sonnet-5` and TypeSafe credentials using `jev-latest`.
No credential values were printed or added to the repository. Foundry's underlying hosting
arrangement was not independently established; an endpoint alone does not identify it.

Dependencies tested: Anthropic 12.29.0, Anthropic.Foundry 0.7.0, Microsoft.Extensions.AI 10.7.0;
SDK 10.0.112/runtime 10.0.12. The hosted tool declaration was `code_execution_20250825`.
Ordinary Foundry and Jev tests passed. Both hosted-execution tests reached the provider and failed
with the same explicit workspace capability rejection. The synthetic CSV upload succeeded and was
deleted in test cleanup. No generated chart or execution container could be verified.

Follow-up on 2026-10-04 at 17:03–17:04 UTC resolved the capability mismatch. Read-only Azure CLI
inspection confirmed `claude-sonnet-5` is model version 2 and the existing `claude-sonnet-4-6`
deployment is version 1, both Global Standard and provisioned successfully. Microsoft's
[deployment instructions](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/use-foundry-models-claude)
identify version 1 as Hosted on Anthropic and version 2 as Hosted on Azure. Anthropic's
[platform documentation](https://platform.claude.com/docs/en/build-with-claude/claude-in-microsoft-foundry#additional-features-not-supported-when-hosted-on-azure)
confirms hosted code execution requires the Anthropic hosting option.

Using the same authorized resource and key with `ANTHROPIC_TEST_MODEL=claude-sonnet-4-6`, all four
Anthropic live tests passed in 36 seconds. The calculation printed 338350; the CSV workflow checked
the total 60, downloaded and verified a PNG, serialized/restored history and reused the container
to read a saved file. No deployment or sample defaults were changed. Direct Anthropic remains
untested, and execution support is verified only for this recorded Foundry route.

At 17:18 UTC, the requested `claude-sonnet-5-5` deployment was also tested on the configured resource.
All four live tests failed with HTTP 404 `DeploymentNotFound`, before model behavior could be
verified. Azure's deployment inventory likewise did not list Sonnet 5.5 on that resource or the
three other Foundry resources checked in the same subscription. `claude-opus-5-5` is present on
the configured resource as version 2, but was only inspected, not live-tested in this follow-up.
At that time Sonnet 5.5 verification required deployment. The user subsequently deployed it;
the successful new-feature verification is recorded above. Evidence is retained in `artifacts/live-test-results/sonnet-5-5/*.trx`.

See [the rerun instructions](runbooks/verify-foundry-code-execution.md) to use the existing secret
store and the verified deployment without changing ordinary chat configuration.

Keep provider credentials locally rather than placing them in source or chat. Run the commands
in the [Anthropic README](../src/FYIsoft.Extensions.AI.Anthropic/README.md#deployment-evidence-and-live-checks)
and [Jev README](../src/FYIsoft.Extensions.AI.Jev/README.md#verification). For Anthropic, run separately
for every direct or Foundry route intended for execution support, recording UTC time, hosting/model,
versions and TRX results. A fixture pass is not evidence that a particular live deployment supports
execution. No additional Foundry project client is introduced by this implementation.

The initial hosted tool profile does not implement newer REPL/programmatic tool-calling profiles.
Unknown complete provider blocks are replayable; unknown streaming delta reconstruction is deliberately
incomplete. Scope metadata guards accidental connection mixing and is not an authorization system.

