# Microsoft.Extensions.AI.Providers

`Microsoft.Extensions.AI` provider SDKs from FYIsoft. MIT licensed.

| Project | Provider | Notes |
|---|---|---|
| [Microsoft.Extensions.AI.Anthropic](src/Microsoft.Extensions.AI.Anthropic/README.md) | Anthropic Claude (API + Azure Foundry) | Full `IChatClient`: streaming, tools |
| [Microsoft.Extensions.AI.Jev](src/Microsoft.Extensions.AI.Jev/README.md) | TypeSafe AI Jev | Typed `JevClient` port of `@typesafe-ai/sdk` v0.6.0, plus an `IChatClient` adapter for structured judgements |

The [SupportDeskSample](samples/SupportDeskSample/README.md) console app uses both providers together. Jev triages each support ticket, Claude drafts a reply with a tool call, and Jev reviews the draft.

## Build

```powershell
dotnet build Microsoft.Extensions.AI.Providers.slnx
dotnet test Microsoft.Extensions.AI.Providers.slnx
dotnet pack Microsoft.Extensions.AI.Providers.slnx -c Release -o artifacts
```

Package versions are managed centrally in `Directory.Packages.props`. Common settings (net10.0, C# 14, nullable, package metadata) live in `Directory.Build.props`.
