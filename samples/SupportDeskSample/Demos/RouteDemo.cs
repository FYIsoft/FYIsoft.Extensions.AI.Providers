using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Jev;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDeskSample.Demos;

/// <summary>
/// Uses Jev's probabilities as a gate: confident tickets route on Jev alone, and only the uncertain ones
/// cost a Claude call. This is the pattern that makes a cheap classifier worth having in front of an LLM.
/// </summary>
public sealed class RouteDemo(
    [FromKeyedServices(SupportDesk.ClaudeKey)] IChatClient claude,
    [FromKeyedServices(SupportDesk.JevKey)] IChatClient jev)
{
    /// <summary>Route on Jev alone above this confidence; below it, ask Claude.</summary>
    private const double AutoRouteThreshold = 0.85;

    /// <summary>
    /// Without this, Claude returns the JSON inside a markdown fence and the structured-output
    /// deserializer rejects the leading backtick.
    /// </summary>
    internal const string ClaudeJsonInstructions =
        "Answer with raw JSON only: no markdown, no code fences and no commentary.";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ConsoleFormat.Heading($"Confidence-banded routing (auto-route at p >= {AutoRouteThreshold:0.00})");

        var tickets = DemoData.Labeled;
        var jevUsage = new UsageDetails();
        var claudeUsage = new UsageDetails();
        var (escalated, correct) = (0, 0);

        Console.WriteLine($"{"#",-3} {"Jev",-12} {"p",6}  {"Decision",-24} {"Final",-10} {"Expected",-10}");

        for (var i = 0; i < tickets.Count; i++)
        {
            var ticket = tickets[i];
            var response = await jev.GetResponseAsync<TicketTriage>(ticket.Text, cancellationToken: cancellationToken);
            Add(jevUsage, response.Usage);

            var jevDepartment = response.Result.Department;
            var confidence = (response.AsJevResponse()?.Answers
                .GetValueOrDefault(nameof(TicketTriage.Department).ToCamelCase()) as JevChoiceAnswer)?.Confidence ?? 0;

            var final = jevDepartment;
            var decision = "auto-routed on Jev";

            if (confidence < AutoRouteThreshold)
            {
                var second = await claude.GetResponseAsync<TicketTriage>(
                    ticket.Text,
                    new ChatOptions { MaxOutputTokens = 200, Instructions = ClaudeJsonInstructions },
                    useJsonSchemaResponseFormat: false,
                    cancellationToken: cancellationToken);
                Add(claudeUsage, second.Usage);

                final = second.Result.Department;
                decision = "escalated to Claude";
                escalated++;
            }

            correct += final == ticket.Department ? 1 : 0;
            Console.WriteLine($"{i + 1,-3} {jevDepartment,-12} {confidence,6:0.00}  {decision,-24} {final,-10} {ticket.Department,-10}");
        }

        ConsoleFormat.Label("Cost of the gate");
        Console.WriteLine($"  Jev ran on all {tickets.Count} tickets: {jevUsage.InputTokenCount ?? 0:N0} input tokens");
        Console.WriteLine($"  Claude ran on {escalated}: {claudeUsage.InputTokenCount ?? 0:N0} in / {claudeUsage.OutputTokenCount ?? 0:N0} out");
        Console.WriteLine($"  Final accuracy: {correct}/{tickets.Count}");
        ConsoleFormat.Note("""
              The gate only works because the probability means something. A model that always reports
              high confidence gives you no band to escalate on.
            """);
    }

    private static void Add(UsageDetails total, UsageDetails? usage)
    {
        if (usage is not null)
        {
            total.Add(usage);
        }
    }
}
