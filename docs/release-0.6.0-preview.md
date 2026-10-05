# Anthropic 0.6.0-preview release

The Anthropic adapter now translates thinking summaries, citations, PDF documents, prompt
caching, hosted web search, and hosted MCP into Microsoft.Extensions.AI types. This release
also includes hosted code execution, file references, and durable provider-history replay.

## Validation

- Release build: zero warnings or errors.
- Offline tests: 120 Anthropic and 42 Jev tests passed.
- Five feature workflows passed on each of Foundry Sonnet 5, Sonnet 5.5, and Opus 5.5.
- Four existing chat/code-execution workflows passed on Foundry Sonnet 4.6.
- Both local NuGet packages restored and executed in a separate package-only consumer.

See [SDK status](sdk-status.md) for detailed evidence and
[the Anthropic README](../src/FYIsoft.Extensions.AI.Anthropic/README.md) for API examples and
provider limitations. Interactive hosted-MCP approval and full raw reasoning are not supported;
incompatible options fail explicitly. Direct Anthropic credentials were not live-tested.

## Credential audit

On 2026-10-04, release preparation checked repository files, reachable Git history, and every
entry extracted from the Anthropic and Jev packages against the locally configured Foundry
and TypeSafe credential values. No matches were found. Checks included UTF-8/UTF-16 binary
representations, and base64 representations in working files and packages. Values were never
printed or written into audit reports.

Gitleaks 8.30.1, obtained from its official release with the archive checksum verified,
independently reported no leaks in the source snapshot, Git history, or package contents.
The NuGet archives contain the library DLL, XML documentation, README, and NuGet metadata.
They contain no application configuration, user-secrets files, live-test results, or logs.
The sample configuration contains blank credential fields. Local credentials remain outside
the repository in the existing .NET user-secrets store.

Detailed audit output and package hashes are retained locally under
`artifacts/release-audit/`, which is excluded from Git and NuGet packages.

## Published to GitHub Packages

On 2026-10-05, Anthropic 0.6.0-preview and Jev 0.1.0-preview were published to the FYIsoft
GitHub Packages NuGet feed. [The publishing workflow](https://github.com/FYIsoft/FYIsoft.Extensions.AI.Providers/actions/runs/37317953231)
passed its Release build, 162 offline tests, package-content check, and a fresh restore/execution
of both packages from the published feed. No provider credentials were used by the workflow.
Downloaded publication artifacts also passed the actual-credential check.

[Installation and future publishing instructions](github-packages.md) cover GitHub authentication;
a NuGet.org account is not required. Exact source commit and package hashes are in
[SDK status](sdk-status.md#github-packages-publication--2026-10-05).
