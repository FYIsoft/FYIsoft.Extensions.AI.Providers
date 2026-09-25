# SupportDeskSample

Console app that uses both providers on the same task: answering customer support tickets.

| Step | Provider | What it shows |
|---|---|---|
| 1. Triage | Jev, through `IChatClient` | `GetResponseAsync<TicketTriage>` structured output: the `bool` becomes a `noul` question, the enum a `choice` question. Raw probabilities come from `AsJevResponse()`. |
| 2. Draft reply | Claude, through `IChatClient` | Streaming, `ChatOptions.Instructions`, and tool calling. `UseFunctionInvocation()` runs `get_account` against an in-memory account list. |
| 3. Review | Jev, through the typed `JevClient` | `SystemOneAsync` with a JSON state (`ticket` + `reply`) and a mix of `noul` and `score` questions. The draft is marked ready to send or flagged for a human. |

Both chat clients are registered as keyed services (`"claude"`, `"jev"`) with `AddKeyedChatClient`. `SupportDesk` receives them through `[FromKeyedServices]`.

## Configure

Keys come from user secrets, which environment variables and `--Section:Key` switches override. From this folder:

```powershell
# Claude on Azure AI Foundry
dotnet user-secrets set "Anthropic:FoundryEndpoint" "https://<resource>.services.ai.azure.com"
dotnet user-secrets set "Anthropic:ApiKey" "<Foundry key>"

# Jev
dotnet user-secrets set "TypeSafe:ApiKey" "<TypeSafe key>"
```

| Setting | Fallback | Default |
|---|---|---|
| `Anthropic:FoundryEndpoint` | — | empty = standard Anthropic API (api.anthropic.com) |
| `Anthropic:ApiKey` | `ANTHROPIC_API_KEY` | required |
| `Anthropic:Model` | — | `claude-sonnet-5` (the Foundry deployment name) |
| `TypeSafe:ApiKey` | `TYPESAFE_API_KEY` | required |
| — | `TYPESAFE_BASE_URL`, `TYPESAFE_DEFAULT_MODEL` | `https://api.typesafe.ai`, `jev-latest` |

## Run

```powershell
# The three built-in tickets
dotnet run --project samples/SupportDeskSample

# One ticket of your own, on a different Claude deployment
dotnet run --project samples/SupportDeskSample -- --ticket "My invoice PDF won't download. - dana@example.com" --Anthropic:Model claude-haiku-4-5
```

The known accounts are `dana@example.com`, `ops@fabrikam.example` and `lee@northwind.example`. Any other email makes `get_account` return "No account is registered to ...", and Claude asks the customer to confirm the address.
