using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Jev;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDeskSample.Demos;

/// <summary>
/// Head-to-head classification: the same labelled tickets through Jev and through Claude, scored on
/// agreement with the human label, latency and tokens.
/// </summary>
/// <remarks>
/// Claude has to be asked for structured output with <c>useJsonSchemaResponseFormat: false</c>, because
/// <see cref="Microsoft.Extensions.AI.Anthropic.AnthropicChatClient"/> does not map
/// <see cref="ChatOptions.ResponseFormat"/> onto Anthropic's schema support; the schema goes in the prompt instead.
/// </remarks>
public sealed class CompareDemo(
    [FromKeyedServices(SupportDesk.ClaudeKey)] IChatClient claude,
    [FromKeyedServices(SupportDesk.JevKey)] IChatClient jev)
{
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ConsoleFormat.Heading("Jev vs Claude: the same classification job");
        ConsoleFormat.Note("Ten tickets, each already routed by a human. Both models see only the ticket text.");
        Console.WriteLine();
        Console.WriteLine($"{"#",-3} {"Expected",-10} {"Jev",-22} {"Claude",-14} {"Jev ms",7} {"Claude ms",10}");

        var tickets = DemoData.Labeled;
        var (jevHits, claudeHits, jevMs, claudeMs) = (0, 0, 0L, 0L);
        var jevUsage = new UsageDetails();
        var claudeUsage = new UsageDetails();

        for (var i = 0; i < tickets.Count; i++)
        {
            var ticket = tickets[i];

            var watch = Stopwatch.StartNew();
            var jevResponse = await jev.GetResponseAsync<TicketTriage>(ticket.Text, cancellationToken: cancellationToken);
            var jevElapsed = watch.ElapsedMilliseconds;

            watch.Restart();
            var claudeResponse = await claude.GetResponseAsync<TicketTriage>(
                ticket.Text,
                new ChatOptions { MaxOutputTokens = 200, Instructions = RouteDemo.ClaudeJsonInstructions },
                useJsonSchemaResponseFormat: false,
                cancellationToken: cancellationToken);
            var claudeElapsed = watch.ElapsedMilliseconds;

            var jevDepartment = jevResponse.Result.Department;
            var claudeDepartment = claudeResponse.Result.Department;
            var confidence = (jevResponse.AsJevResponse()?.Answers
                .GetValueOrDefault(nameof(TicketTriage.Department).ToCamelCase()) as JevChoiceAnswer)?.Confidence;

            jevHits += jevDepartment == ticket.Department ? 1 : 0;
            claudeHits += claudeDepartment == ticket.Department ? 1 : 0;
            jevMs += jevElapsed;
            claudeMs += claudeElapsed;
            Add(jevUsage, jevResponse.Usage);
            Add(claudeUsage, claudeResponse.Usage);

            var jevCell = $"{Mark(jevDepartment == ticket.Department)} {jevDepartment} ({confidence:P0})";
            Console.WriteLine($"{i + 1,-3} {ticket.Department,-10} {jevCell,-22} {Mark(claudeDepartment == ticket.Department)} {claudeDepartment,-12} {jevElapsed,7} {claudeElapsed,10}");
        }

        ConsoleFormat.Label("Totals");
        Console.WriteLine($"  Jev     {jevHits}/{tickets.Count} correct   {jevMs / tickets.Count,5} ms avg   {jevUsage.InputTokenCount ?? 0,6:N0} in / {jevUsage.OutputTokenCount ?? 0,5:N0} out");
        Console.WriteLine($"  Claude  {claudeHits}/{tickets.Count} correct   {claudeMs / tickets.Count,5} ms avg   {claudeUsage.InputTokenCount ?? 0,6:N0} in / {claudeUsage.OutputTokenCount ?? 0,5:N0} out");
        ConsoleFormat.Note("""
              Jev answers with a probability per label, so a borderline ticket is visible before it is wrong.
              Claude returns one label and no calibrated alternative, but it can explain itself and handle
              anything the label set does not cover.
            """);
    }

    private static string Mark(bool hit) => hit ? "+" : "x";

    private static void Add(UsageDetails total, UsageDetails? usage)
    {
        if (usage is not null)
        {
            total.Add(usage);
        }
    }
}

internal static class StringCasing
{
    /// <summary>Schema property names reach Jev camel-cased, so answers come back keyed that way.</summary>
    public static string ToCamelCase(this string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToLowerInvariant(value[0]) + value[1..];
}
