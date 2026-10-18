# FYIsoft.Extensions.AI.Decision1

Typed .NET client for **Microsoft-Decision-1**, the decision model in Microsoft Foundry. Targets .NET 10.

Microsoft-Decision-1 is not a chat model. It does not write text or explain itself. You send it a
*state* (text or a JSON object) and one or more bounded questions, and it returns numbers your
code can threshold or rank:

| Question | Type | Answer |
|---|---|---|
| Is this true? | `noul` | A probability from 0 through 1 |
| Which option is it? | `choice` | The selected option, a confidence, and a probability for every option |
| How much? | `score` | A probability-weighted level index, a confidence, and a probability for every level |

Use it for gates and filters, routing, classification, prioritization and grading. Use a generative
model when you need text. This package has **no `IChatClient` adapter**; one could be added later
for structured judgements, but the typed client is the supported surface in this version.

> **Preview.** Microsoft-Decision-1 is a Preview model. Version 1 retires on **2027-02-04**
> according to the Foundry model catalog. Plan to move to a later version before then; the
> deployment name comes from options, so a new version is a configuration change.

Install from [FYIsoft GitHub Packages](https://github.com/FYIsoft/FYIsoft.Extensions.AI.Providers/blob/main/docs/github-packages.md):

```powershell
dotnet add package FYIsoft.Extensions.AI.Decision1 --prerelease
```

The package depends on `Azure.Core` for `TokenCredential`. Add `Azure.Identity` yourself for
`DefaultAzureCredential` or another credential type.

## Configure

You need the Foundry resource endpoint and the **deployment name** (not the model name). Both
endpoint forms work: `https://{name}.services.ai.azure.com` and `https://{name}.cognitiveservices.azure.com`.

### Microsoft Entra ID (recommended)

```csharp
using Azure.Identity;
using Microsoft.Extensions.AI.Decision1;

using var client = new Decision1Client(new Decision1ClientOptions
{
    Endpoint = new Uri("https://my-resource.services.ai.azure.com"),
    Deployment = "decision-1",
    Credential = new DefaultAzureCredential(),
});
```

The scope is `https://cognitiveservices.azure.com/.default`. The token is cached and refreshed
five minutes before it expires (or at the credential's `RefreshOn`); concurrent calls share one
refresh. A 401 drops the cached token so the next call gets a new one. The identity needs a
data-plane role assignment on the Foundry resource; without one the service answers 401
`PermissionDenied` (see [Verified service behavior](#verified-service-behavior)).

### API key

```csharp
var options = new Decision1ClientOptions
{
    Endpoint = new Uri("https://my-resource.services.ai.azure.com"),
    Deployment = "decision-1",
    ApiKey = apiKeyFromYourSecretStore, // sent as the api-key header
};
```

Set exactly one of `Credential` or `ApiKey`; setting both throws `ArgumentException`.

### Environment variables

Null settings fall back to environment variables (disable with `UseEnvironmentVariables = false`):

| Variable | Used when |
|---|---|
| `DECISION1_ENDPOINT` | `Endpoint` is null |
| `DECISION1_DEPLOYMENT` | `Deployment` is null |
| `DECISION1_API_KEY` | both `Credential` and `ApiKey` are null |

`Decision1ClientOptions.FromEnvironment()` returns options that read all three.

### Dependency injection

```csharp
using Microsoft.Extensions.AI.Decision1;

// In code (and/or DECISION1_* variables)
services.AddDecision1Client(o =>
{
    o.Endpoint = new Uri("https://my-resource.services.ai.azure.com");
    o.Deployment = "decision-1";
    o.Credential = new DefaultAzureCredential();
});

// Or bind the "Decision1" configuration section, then add the credential in code
services.AddDecision1Client(builder.Configuration.GetSection(Decision1ClientOptions.SectionName),
    o => o.Credential = new DefaultAzureCredential());

// Inject IDecision1Client
public sealed class TicketRouter(IDecision1Client decisions) { /* ... */ }
```

`AddDecision1Client` registers `IDecision1Client` as a typed `HttpClient` through
`IHttpClientFactory` (client name `Decision1`) and returns the `IHttpClientBuilder`. Resolved
clients are transient; one token cache is shared per service provider. Options are validated
when the client is first resolved. The client retries on its own, so do not add a second retry
handler to the builder (or set `Retry.MaxRetries = 0` if you do).

Configuration keys: `Endpoint`, `Deployment`, `ApiKey`, `Timeout` (for example `00:00:05`),
`TokenScope`, `UseEnvironmentVariables`, `Retry:MaxRetries`, `Retry:InitialDelay`,
`Retry:MaxDelay`, `Retry:MaxRetryAfter`, `Retry:JitterRatio`. Keep `ApiKey` in a secret store.

## Ask questions

### Noul: is this true?

```csharp
var refund = await client.DecideAsync(
    "I refunded your $200. You don't need to contact billing.",
    Decision1Question.Noul(
        "Does this reply promise the customer a refund?",
        whenTrue: "Commits to returning money",
        whenFalse: "Makes no commitment about money"),
    name: "promises_refund");

if (refund.IsTrue(threshold: 0.8)) { /* hold the reply for review */ }
Console.WriteLine(refund.Noul); // probability of true, 0..1
```

The criteria are optional (`Decision1Question.Noul("...")`). Choose the threshold from labeled
examples of your own workload; `IsTrue()` defaults to 0.5.

### Choice: which option?

```csharp
var team = await client.DecideAsync(
    "The API returns 500 on every call.",
    Decision1Question.Choice("Which team should handle this ticket?", new Dictionary<string, string>
    {
        ["billing"] = "Charges, invoices, and refunds",
        ["engineering"] = "Bugs, errors, and outages",
        ["support"] = "How-to and account questions",
    }),
    name: "team");

Console.WriteLine($"{team.Choice} ({team.Confidence:P0})");
foreach (var (label, probability) in team.Probabilities)
    Console.WriteLine($"  {label}: {probability:0.000}");
```

Options are label-to-description pairs and are sent as a JSON object in your order (the service
rejects an array). A low top probability is a signal to escalate. Consider an abstain option such
as `cannot_tell`.

### Score: how much?

```csharp
var severity = await client.DecideAsync(
    "Checkout is down for all EU customers since 09:00.",
    Decision1Question.Score("How severe is this incident?", "Cosmetic", "Minor", "Major", "Critical"),
    name: "severity");

Console.WriteLine(severity.Score);                   // e.g. 2.2: weighted index, 0..3 here
Console.WriteLine(severity.NearestLevel.Label);      // "Major" (Score rounded; client-derived)
Console.WriteLine(severity.MostProbableLevel.Label); // highest-probability level (client-derived)
Console.WriteLine(severity.Probabilities[3]);        // by index
Console.WriteLine(severity.ProbabilitiesByLabel["Critical"]); // by label
```

List levels lowest first. `Score` is the probability-weighted average of the level indexes, so it
can fall between levels. `Levels` holds each level's index, label and probability in order;
`Legend` holds the labels. Prefer scores for relative ordering and thresholds.

### Several questions in one request

Questions about the same state can share one request, which is cheaper and keeps the context consistent.

```csharp
var response = await client.DecideAsync(
    new JsonObject
    {
        ["message"] = "Checkout is down again. This is my third request.",
        ["customer_contact_count"] = 3,
    },
    new Dictionary<string, Decision1Question>
    {
        ["team"] = Decision1Question.Choice("Which team should handle this request?", teams),
        ["severity"] = Decision1Question.Score("How severe is this issue?", "Cosmetic", "Minor", "Major", "Critical"),
        ["repeat_contact"] = Decision1Question.Noul("Has the customer contacted support before?"),
    },
    cancellationToken);

var team = response.GetAnswer<Decision1ChoiceAnswer>("team");
var severity = response.GetAnswer<Decision1ScoreAnswer>("severity");
var repeat = response.GetAnswer<Decision1NoulAnswer>("repeat_contact");

Console.WriteLine($"{response.Model}: {response.Usage.InputTokens} in, {response.Usage.OutputTokens} out");
Console.WriteLine($"request {response.Metadata.RequestId} in {response.Metadata.Region}; " +
                  $"{response.Metadata.RateLimitRemainingRequests} requests left, reset in {response.Metadata.RateLimitResetRequests}");
```

`GetAnswer<T>` throws `KeyNotFoundException` (listing the names that exist) for a missing name and
`InvalidCastException` for the wrong type; `TryGetAnswer<T>` does not throw. `response.Model` is the
underlying model (`microsoft-decision-1`), not your deployment name. Answer types this SDK does
not know become `Decision1UnknownAnswer`; every answer keeps the service JSON in `RawJson`.

The single-question overloads return only the typed answer. Use the dictionary overload when you
need usage or metadata.

### State

`Decision1State` accepts a `string`, a `JsonElement` or a `JsonNode` implicitly. For other objects
use `Decision1State.FromObject(value)`; it is serialized with `Decision1ClientOptions.JsonSerializerOptions`
(default `JsonSerializerOptions.Web`, camelCase). State must be text or a JSON object; arrays,
numbers, booleans and null are rejected on the client.

## Errors

| Situation | Exception |
|---|---|
| Invalid question, options or state (checked before sending) | `ArgumentException` |
| HTTP 400 (`unsupported_request_argument`) | `Decision1BadRequestException` |
| HTTP 401 | `Decision1AuthenticationException` |
| HTTP 403 | `Decision1PermissionDeniedException` |
| HTTP 404 (`DeploymentNotFound`) | `Decision1NotFoundException` |
| HTTP 422 | `Decision1UnprocessableEntityException` |
| HTTP 429 after retries | `Decision1RateLimitException` (`RetryAfter`) |
| HTTP 5xx after retries | `Decision1ServerException` |
| Other non-success status | `Decision1ApiException` |
| Connection failure after retries | `Decision1ConnectionException` |
| Every attempt timed out | `Decision1TimeoutException` (a `Decision1ConnectionException`) |
| Unparseable success body | `Decision1Exception` |
| Your cancellation token | `OperationCanceledException` |

Every API exception derives from `Decision1ApiException` and `Decision1Exception` and carries
`StatusCode`, `ErrorCode` (from `error.code`, else the `x-ms-error-code` header), `ErrorMessage`,
`Details`, `RequestId` (`apim-request-id`) and `Metadata`. Exception messages never contain the
request body or credentials; quote the request id to Microsoft support.

```csharp
try
{
    var answer = await client.DecideAsync(state, question, "team", ct);
}
catch (Decision1RateLimitException ex)
{
    // Retries were exhausted. ex.RetryAfter is the server's hint.
}
catch (Decision1ApiException ex) when (ex.ErrorCode == "DeploymentNotFound")
{
    // Wrong deployment name, or created less than five minutes ago.
}
```

## Retries and timeouts

- Each attempt has its own timeout: `Timeout`, default **10 seconds** (measured p50 latency is about
  120 ms). Token acquisition and retry delays are not counted.
- Retried: HTTP 408, 429 and 5xx, connection failures and attempt timeouts. **Never retried:** any
  other 4xx.
- `Retry.MaxRetries` defaults to 2 (three attempts). Backoff starts at `InitialDelay` (500 ms),
  doubles each retry, is capped at `MaxDelay` (8 s) and jittered down by up to `JitterRatio` (25%).
- Server hints win over backoff: `retry-after-ms`, then `Retry-After` (seconds or date), then, for
  429 only, `x-ratelimit-reset-requests` (seconds). Hints are capped at `MaxRetryAfter` (30 s).
- Retries resend the same body and reuse the cached token. Your cancellation token stops the
  request, the token fetch and any retry delay.

## Limits

Checked on the client with the service's rules, so you get an `ArgumentException` without a call:

- at least one question; names non-empty and unique (ordinal)
- instructions non-empty
- choice: **2 to 255** options; labels non-empty and unique; descriptions not null
- score: **2 to 10** levels; labels non-empty
- noul criteria: both `whenTrue` and `whenFalse`, or neither

Two client rules are stricter than anything verified on the server: score level labels must be
unique (so label lookups are unambiguous), and state must be text or a JSON object.

Rate limits are per deployment (the test deployment allowed 30 requests per 60 seconds). Read
`Metadata.RateLimitRemainingRequests` and `RateLimitResetRequests` to pace bulk work.

## Verified service behavior

A raw HTTP probe of a real deployment on 2026-10-18 established the wire contract this client implements:

- `POST {endpoint}/providers/microsoft/v1/systemone` on either host form. `api-version` is
  accepted and ignored, so the client sends none.
- Auth by `Authorization: Bearer` (scope `https://cognitiveservices.azure.com/.default`) or `api-key`.
- Request: `{ "model": "<deployment>", "state": <string or object>, "questions": { "<name>": { "type", "instructions", "criteria"? } } }`.
  Choice criteria must be an object (an array returns 400); score criteria are an array.
- Response: `{ "model": "microsoft-decision-1", "answers": { ... }, "usage": { "input_tokens", "output_tokens" } }`.
  Score answers carry `legend` and `probabilities` keyed `"0"`..`"n-1"`.
- Errors: `{ "error": { "code", "message", "details" } }` plus `x-ms-error-code`. Seen: 400
  `unsupported_request_argument` (bad type, empty questions, 11 levels, choice array), 401 code
  `"401"` (no auth), 404 `DeploymentNotFound`. The docs also list 403, 422 and 429.
- The SDK's own live run on the same day also saw **401 with code `PermissionDenied`** for an Entra
  identity without data-plane access to the resource (the docs describe that case as 403). Both
  map to `Decision1AuthenticationException`; check `ErrorCode`.
- Headers surfaced in `Metadata`: `apim-request-id`, `x-ms-region`, `x-ratelimit-limit-requests`,
  `x-ratelimit-remaining-requests`, `x-ratelimit-reset-requests`, `x-ratelimit-renewalperiod-requests`.

Reference: [Deploy and use Microsoft-Decision-1 in Microsoft Foundry](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/use-foundry-models-microsoft-decision).

## Verification and live tests

Offline tests use a synthetic `HttpMessageHandler` and the real probe bodies as fixtures. They
cover request JSON for every question type, parsing, client-side validation, error mapping,
retries, token caching, cancellation, DI and environment fallback.

Live tests make three requests (one combined noul/choice/score request, one server-side 400 and
one 404) and are skipped unless explicitly enabled:

```powershell
$env:DECISION1_LIVE_TESTS = '1'
$env:DECISION1_ENDPOINT = 'https://my-resource.services.ai.azure.com'
$env:DECISION1_DEPLOYMENT = 'decision-1'
# Uses DefaultAzureCredential unless DECISION1_API_KEY is set.
# If AZURE_CLIENT_ID/AZURE_CLIENT_SECRET belong to another principal, pick the az login identity:
$env:AZURE_TOKEN_CREDENTIALS = 'AzureCliCredential'
dotnet test tests/FYIsoft.Extensions.AI.Decision1.Tests -c Release --filter 'Category=Live' --logger trx
```

Use synthetic state only. Results are recorded in [SDK completion status](https://github.com/FYIsoft/FYIsoft.Extensions.AI.Providers/blob/main/docs/sdk-status.md).
