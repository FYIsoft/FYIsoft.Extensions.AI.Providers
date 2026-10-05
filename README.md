# FYIsoft.Extensions.AI.Providers

`Microsoft.Extensions.AI` provider SDKs from FYIsoft. MIT licensed.

| Project | Provider | Notes |
|---|---|---|
| [FYIsoft.Extensions.AI.Anthropic](src/FYIsoft.Extensions.AI.Anthropic/README.md) | Anthropic Claude (API + Foundry) | `IChatClient`: thinking, citations, PDFs, caching, web search, MCP, functions, hosted execution and durable replay |
| [FYIsoft.Extensions.AI.Jev](src/FYIsoft.Extensions.AI.Jev/README.md) | TypeSafe AI Jev | Typed `JevClient` port of `@typesafe-ai/sdk` v0.6.0, plus an `IChatClient` adapter for structured judgements |

The [SupportDeskSample](samples/SupportDeskSample/README.md) console app uses both providers together. Jev triages each support ticket, Claude drafts a reply with a tool call, and Jev reviews the draft.

## Install

Both SDKs can be installed from the [FYIsoft GitHub Packages feed](docs/github-packages.md).
The guide covers authentication, installation, package access, and publishing new versions.
No NuGet.org account is needed.

## Build

```powershell
dotnet build FYIsoft.Extensions.AI.Providers.slnx
dotnet test FYIsoft.Extensions.AI.Providers.slnx
dotnet pack FYIsoft.Extensions.AI.Providers.slnx -c Release -o artifacts
```

Package versions are managed centrally in `Directory.Packages.props`. Common settings (net10.0, C# 14, nullable, package metadata) live in `Directory.Build.props`.

See [SDK completion status](docs/sdk-status.md) for verification evidence and remaining live release gates.
Tests use synthetic transports by default. Live tests require explicit opt-in (`ANTHROPIC_LIVE_TESTS=1`
or `JEV_LIVE_TESTS=1`) and configured credentials; see each provider README.
