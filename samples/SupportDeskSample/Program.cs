using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Anthropic;
using Microsoft.Extensions.AI.Jev;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SupportDeskSample;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
});
// User secrets sit under the sources Host.CreateApplicationBuilder already added, so environment
// variables and command-line switches still override them.
builder.Configuration.AddUserSecrets<Program>(optional: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

var config = builder.Configuration;
var anthropicKey = config["Anthropic:ApiKey"] is { Length: > 0 } key ? key : Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
var typeSafeKey = config["TypeSafe:ApiKey"] is { Length: > 0 } tsKey ? tsKey : Environment.GetEnvironmentVariable(JevClientOptions.ApiKeyEnvironmentVariable);

if (string.IsNullOrWhiteSpace(anthropicKey) || string.IsNullOrWhiteSpace(typeSafeKey))
{
    Console.Error.WriteLine("""
        Missing API keys. From samples/SupportDeskSample run:

          dotnet user-secrets set "Anthropic:FoundryEndpoint" "https://<resource>.services.ai.azure.com"
          dotnet user-secrets set "Anthropic:ApiKey" "<Foundry key>"
          dotnet user-secrets set "TypeSafe:ApiKey" "<TypeSafe key>"

        Leave Anthropic:FoundryEndpoint unset to call api.anthropic.com with an Anthropic API key instead.
        ANTHROPIC_API_KEY and TYPESAFE_API_KEY environment variables also work.
        """);
    return 1;
}

// Claude drafts the replies. UseFunctionInvocation runs the tool calls Claude asks for and sends the results back.
builder.Services.AddKeyedChatClient(SupportDesk.ClaudeKey, sp =>
    {
        var model = config["Anthropic:Model"];
        var logger = sp.GetRequiredService<ILogger<AnthropicChatClient>>();

        // The Foundry resource name is the endpoint's first host label: https://<resource>.services.ai.azure.com
        return config["Anthropic:FoundryEndpoint"] is { Length: > 0 } endpoint
            ? new AnthropicChatClient(anthropicKey, new Uri(endpoint).Host.Split('.')[0], model, logger)
            : new AnthropicChatClient(new AnthropicClient(new ClientOptions { ApiKey = anthropicKey }), model, logger);
    })
    .UseFunctionInvocation();

// Jev triages tickets through IChatClient and reviews drafts through the typed JevClient.
builder.Services.AddJevClient(o => o.ApiKey = typeSafeKey);
builder.Services.AddKeyedChatClient(SupportDesk.JevKey, sp => sp.GetRequiredService<JevClient>().AsIChatClient());

builder.Services.AddSingleton<AccountDirectory>();
builder.Services.AddSingleton<SupportDesk>();

using var host = builder.Build();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

// dotnet run -- --ticket "..." handles one ticket; otherwise the built-in samples run.
IReadOnlyList<string> tickets = config["ticket"] is { Length: > 0 } ticket ? [ticket] : SampleTickets.All;

try
{
    await host.Services.GetRequiredService<SupportDesk>().RunAsync(tickets, cts.Token);
    return 0;
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
    Console.WriteLine();
    Console.WriteLine("Cancelled.");
    return 130;
}
