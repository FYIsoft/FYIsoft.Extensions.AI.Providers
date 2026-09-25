# FYIsoft.Extensions.AI.Anthropic

`IChatClient` implementation for Anthropic's Claude models, supporting both the standard Anthropic API and Azure Anthropic Foundry.

```csharp
// Standard API (reads ANTHROPIC_API_KEY when apiKey is null)
services.AddAnthropicChatClient(apiKey: null, modelId: "claude-sonnet-4-5");

// Azure Foundry
IChatClient chat = new AnthropicChatClient(apiKey, resourceName, modelId: "claude-sonnet-4-5");
```

Features: system-message extraction, tool calling, streaming, usage reporting, and one retry on transient 5xx/IO failures.
