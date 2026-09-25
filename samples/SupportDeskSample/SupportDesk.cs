using System.ComponentModel;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Jev;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDeskSample;

public enum Department
{
    Billing,
    Technical,
    Sales,
}

/// <summary>Jev derives a noul question from the bool and a choice question from the enum.</summary>
public sealed record TicketTriage(
    [property: Description("Does the customer need a response urgently?")] bool IsUrgent,
    [property: Description("Which team should handle this ticket?")] Department Department);

/// <summary>
/// Runs each ticket through three steps:
/// Jev triages it, Claude drafts a reply (looking up the account with a tool), and Jev reviews the draft.
/// </summary>
public sealed class SupportDesk(
    [FromKeyedServices(SupportDesk.ClaudeKey)] IChatClient claude,
    [FromKeyedServices(SupportDesk.JevKey)] IChatClient jev,
    JevClient jevClient,
    AccountDirectory accounts)
{
    public const string ClaudeKey = "claude";
    public const string JevKey = "jev";

    private readonly UsageDetails _claudeUsage = new();
    private readonly UsageDetails _jevUsage = new();

    public async Task RunAsync(IReadOnlyList<string> tickets, CancellationToken cancellationToken)
    {
        var claudeMetadata = claude.GetService<ChatClientMetadata>();
        WriteLine($"Claude: {claudeMetadata?.DefaultModelId} via {claudeMetadata?.ProviderName}", ConsoleColor.DarkGray);
        WriteLine($"Jev:    {jevClient.DefaultModel} via {jevClient.BaseUrl}", ConsoleColor.DarkGray);

        for (var i = 0; i < tickets.Count; i++)
        {
            var ticket = tickets[i].Trim();

            Heading($"Ticket {i + 1} of {tickets.Count}");
            Console.WriteLine(ticket);

            var triage = await TriageAsync(ticket, cancellationToken);
            var reply = await DraftReplyAsync(ticket, triage, cancellationToken);
            await ReviewAsync(ticket, reply, cancellationToken);
        }

        Heading("Usage");
        Console.WriteLine($"Claude  {_claudeUsage.InputTokenCount ?? 0,7:N0} in  {_claudeUsage.OutputTokenCount ?? 0,7:N0} out");
        Console.WriteLine($"Jev     {_jevUsage.InputTokenCount ?? 0,7:N0} in  (Jev bills input tokens only)");
    }

    /// <summary>Step 1: Jev classifies the ticket through the IChatClient adapter's structured output.</summary>
    private async Task<TicketTriage> TriageAsync(string ticket, CancellationToken cancellationToken)
    {
        var response = await jev.GetResponseAsync<TicketTriage>(ticket, cancellationToken: cancellationToken);
        AddUsage(_jevUsage, response.Usage);

        Label("Jev triage");
        foreach (var (question, answer) in response.AsJevResponse()?.Answers ?? new Dictionary<string, JevAnswer>())
        {
            Console.WriteLine($"  {question,-12} {Describe(answer)}");
        }

        return response.Result;
    }

    /// <summary>Step 2: Claude streams a reply, calling get_account first.</summary>
    private async Task<string> DraftReplyAsync(string ticket, TicketTriage triage, CancellationToken cancellationToken)
    {
        var options = new ChatOptions
        {
            Instructions = $"""
                You are a support agent at Contoso Cloud. Write the reply to the customer's ticket.
                Call get_account with the customer's email before replying, and base every statement about
                charges, incidents or plans on what it returns. Do not promise anything the account data does not support.
                Triage: {triage.Department} team, {(triage.IsUrgent ? "urgent" : "not urgent")}.
                Output only the reply: plain text, under 120 words, signed "Contoso Support".
                """,
            MaxOutputTokens = 1024,
            Tools = [accounts.GetAccountTool],
        };

        Label("Claude draft");

        // Text streamed before a tool call is preamble; only the text after the last tool result is the reply.
        var reply = new StringBuilder();
        await foreach (var update in claude.GetStreamingResponseAsync(ticket, options, cancellationToken))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent call:
                        WriteLine($"  [tool] {call.Name}({string.Join(", ", call.Arguments?.Select(a => $"{a.Key}: {a.Value}") ?? [])})", ConsoleColor.DarkYellow);
                        break;
                    case FunctionResultContent:
                        reply.Clear();
                        break;
                    case TextContent text:
                        Console.Write(text.Text);
                        reply.Append(text.Text);
                        break;
                    case UsageContent usage:
                        AddUsage(_claudeUsage, usage.Details);
                        break;
                }
            }
        }

        Console.WriteLine();
        return reply.ToString().Trim();
    }

    /// <summary>Step 3: Jev grades the draft with the typed client, mixing noul and score questions.</summary>
    private async Task ReviewAsync(string ticket, string reply, CancellationToken cancellationToken)
    {
        var review = await jevClient.SystemOneAsync(
            state: new { ticket, reply },
            questions: new Dictionary<string, JevQuestion>
            {
                ["resolves"] = JevQuestion.Noul("Does the reply address the customer's actual problem with a concrete next step?"),
                ["tone"] = JevQuestion.Score("How empathetic is the reply's tone?", "Cold", "Neutral", "Warm"),
            },
            cancellationToken: cancellationToken);
        AddUsage(_jevUsage, new UsageDetails { InputTokenCount = review.Usage.InputTokens });

        Label("Jev review");
        foreach (var (question, answer) in review.Answers)
        {
            Console.WriteLine($"  {question,-12} {Describe(answer)}");
        }

        var approved = review.GetAnswer<JevNoulAnswer>("resolves").IsYes && review.GetAnswer<JevScoreAnswer>("tone").Score >= 1;
        WriteLine(approved ? "  -> ready to send" : "  -> needs a human", approved ? ConsoleColor.Green : ConsoleColor.Red);
    }

    private static string Describe(JevAnswer answer) => answer switch
    {
        JevNoulAnswer noul => $"{(noul.IsYes ? "yes" : "no")} (p = {noul.Noul:0.00})",
        JevChoiceAnswer choice => $"{choice.Choice} ({choice.Confidence:P0} confidence)",
        JevScoreAnswer score =>
            $"{score.Score:0.00} on 0-{score.Legend.Count - 1}, most likely \"{score.Legend.GetValueOrDefault(score.MostLikelyLevel.ToString())}\"",
        _ => $"({answer.Type})",
    };

    private static void AddUsage(UsageDetails total, UsageDetails? usage)
    {
        if (usage is not null)
        {
            total.Add(usage);
        }
    }

    private static void Heading(string text) => ConsoleFormat.Heading(text);

    private static void Label(string text) => ConsoleFormat.Label(text);

    private static void WriteLine(string text, ConsoleColor color) => ConsoleFormat.Write(text, color);
}
