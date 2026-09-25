# FYIsoft.Extensions.AI.Jev

.NET client for [TypeSafe AI](https://docs.typesafe.ai)'s **Jev** model, ported from the `@typesafe-ai/sdk` JavaScript SDK (v0.6.0), with a `Microsoft.Extensions.AI` `IChatClient` adapter.

Jev is a "System One" model. It does **not** generate text, stream, or call tools. You give it a *state* (text or JSON) and a set of typed questions, and it returns calibrated probabilities:

| Question | Answer |
|---|---|
| `noul` (yes/no) | `Noul` — probability of yes |
| `choice` (≤ 255 labels) | `Choice`, per-label `Probabilities`, `Confidence` |
| `score` (2–10 ordered levels) | probability-weighted `Score`, `Legend`, `Probabilities`, `Confidence` |

## Configuration

| Option | Environment variable | Default |
|---|---|---|
| `ApiKey` | `TYPESAFE_API_KEY` | required |
| `BaseUrl` | `TYPESAFE_BASE_URL` | `https://api.typesafe.ai` |
| `DefaultModel` | `TYPESAFE_DEFAULT_MODEL` | `jev-latest` |
| `Timeout` (per attempt) | — | 10 s |
| `Retry` | — | 2 retries, 500 ms → 5 s exponential backoff, 25% jitter; retries 408/429/5xx, connection errors and timeouts; honors `retry-after-ms` / `Retry-After` up to 60 s |

## Typed client

```csharp
using Microsoft.Extensions.AI.Jev;

using var client = new JevClient(); // reads TYPESAFE_API_KEY

var response = await client.SystemOneAsync(
    state: new { document = "I was charged twice. Please fix this ASAP." },
    questions: new Dictionary<string, JevQuestion>
    {
        ["is_urgent"] = JevQuestion.Noul("Does this convey urgency?"),
        ["category"] = JevQuestion.Choice("What is this ticket about?", "billing", "technical", "other"),
        ["frustration"] = JevQuestion.Score("How frustrated is the customer?", "Calm", "Frustrated", "Very angry"),
    });

var category = response.GetAnswer<JevChoiceAnswer>("category");
Console.WriteLine($"{category.Choice} ({category.Confidence:P0})");
Console.WriteLine(response.GetAnswer<JevNoulAnswer>("is_urgent").Noul);

var models = await client.ListModelsAsync();
```

Errors derive from `JevException`: `JevApiException` (with `JevBadRequestException`, `JevAuthenticationException`, `JevPermissionDeniedException`, `JevNotFoundException`, `JevUnprocessableEntityException`, `JevRateLimitException`, `JevInternalServerException`) and `JevConnectionException` / `JevTimeoutException`. Caller cancellation surfaces as `OperationCanceledException`.

## IChatClient adapter

```csharp
services.AddJevChatClient(o => o.ApiKey = config["TypeSafe:ApiKey"]);
// or: IChatClient chat = new JevClient().AsIChatClient();
```

**Structured output** — questions are derived from the response schema. `bool` properties become `noul` (true when p ≥ 0.5), enum properties become `choice`, and `[Description]` becomes the instructions:

```csharp
public enum Department { Billing, Technical, Sales }

public record TicketTriage(
    [property: Description("Does this convey urgency?")] bool IsUrgent,
    [property: Description("Which team should handle this?")] Department Department);

var triage = await chat.GetResponseAsync<TicketTriage>(ticketText);
var probabilities = triage.AsJevResponse()!.Answers; // raw probabilities are still available
```

**Explicit questions** (needed for `score`) — the response text is the answers JSON:

```csharp
var options = new ChatOptions().WithJevQuestions(new Dictionary<string, JevQuestion>
{
    ["frustration"] = JevQuestion.Score("How frustrated is the customer?", "Calm", "Frustrated", "Very angry"),
});
var response = await chat.GetResponseAsync(ticketText, options);
```

Mapping rules:

- A single user text message is sent as a string state. Anything else is sent as `{"messages":[{"from":"<role>","text":"..."}]}`, including system messages and `ChatOptions.Instructions`.
- Non-text content (images, files) throws `NotSupportedException`.
- Streaming returns the complete response as one update.
- `ChatOptions.Tools` is ignored.
- `UsageDetails` is filled from `usage`. Only input tokens are billed.
