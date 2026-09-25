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

## Advanced demos

```powershell
dotnet run --project samples/SupportDeskSample -- --demo compare   # Jev vs Claude on the same job
dotnet run --project samples/SupportDeskSample -- --demo route     # confidence-banded escalation
dotnet run --project samples/SupportDeskSample -- --demo rubric    # four-dimension review + revise loop
dotnet run --project samples/SupportDeskSample -- --demo limits    # what Jev cannot do
```

**`compare`** runs ten human-labelled tickets through both models and reports agreement, latency and tokens. A representative run: Jev 8/10 at ~200 ms per ticket, Claude 9/10 at ~1.4 s. Jev's two misses came back at 65% and 35% confidence, so both were visible as doubtful before they were wrong.

**`route`** uses that calibration as a gate. Tickets above `p >= 0.85` route on Jev alone; the rest escalate to Claude. In a representative run 5 of 10 escalated and final accuracy was 9/10 — better than either model alone, because Jev's low-confidence answers are exactly the ones Claude fixes.

**`rubric`** asks four judgements (three `noul`, one `score`) in a *single* Jev call against a state holding the ticket, the account record and the draft, then feeds the failures back to Claude as revision notes and re-reviews. Watch for revision 1 fixing the tone but introducing an over-promise that round 2 then removes.

**`limits`** is the honest half. Every case fails, most before a request is sent:

| Asked for | Outcome |
|---|---|
| Extract the order number (string output) | `NotSupportedException` — only bool and enum properties map to questions |
| Return a numeric severity (`int`) | `NotSupportedException` — same reason |
| Judge an image attachment | `NotSupportedException` — Jev is text-only |
| Answer an open-ended prompt | `InvalidOperationException` — Jev needs questions |
| Choose between 300 labels | `ArgumentException` — the maximum is 255 |
| Score on a single level | `ArgumentException` — 2 to 10 levels required |
| Call a tool / stream | No error: `Tools` is dropped, and the call completes before the first update |

The short version: anything that has to produce words, call a tool, read an image or stream belongs to Claude. Jev only answers questions you can pose as yes/no, a fixed label set, or an ordered scale.
