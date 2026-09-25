using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Jev;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDeskSample.Demos;

/// <summary>
/// A four-dimension rubric answered in a single Jev call, then fed back to Claude as revision notes.
/// Jev grades, Claude writes: neither could do the other's half.
/// </summary>
public sealed class RubricDemo(
    [FromKeyedServices(SupportDesk.ClaudeKey)] IChatClient claude,
    JevClient jevClient,
    AccountDirectory accounts)
{
    private const string Email = "dana@example.com";
    private const int MaxRevisions = 2;

    private static readonly string[] Requirements = ["grounded", "actionable", "no_overpromise"];

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ConsoleFormat.Heading("A rubric Jev answers in one call, and a revise loop");

        var ticket = DemoData.Labeled[0].Text;
        var account = JsonSerializer.Serialize(AccountDirectory.Lookup(Email));

        ConsoleFormat.Label("Claude's draft");
        var draft = await DraftAsync(ticket, cancellationToken);
        Console.WriteLine(draft);
        var review = await ReviewAsync(ticket, account, draft, cancellationToken);
        Report(review);

        ConsoleFormat.Label("The same rubric on a weak reply");
        Console.WriteLine(DemoData.WeakDraft);
        var weakReview = await ReviewAsync(ticket, account, DemoData.WeakDraft, cancellationToken);
        Report(weakReview);

        // Revise whichever draft the rubric rejected. If Claude's own draft passed, repair the weak one.
        var (current, currentReview) = Failures(review).Count > 0 ? (draft, review) : (DemoData.WeakDraft, weakReview);

        for (var round = 1; round <= MaxRevisions && Failures(currentReview).Count > 0; round++)
        {
            var failures = Failures(currentReview);
            ConsoleFormat.Label($"Revision {round}: fixing {string.Join(", ", failures)}");

            current = await ReviseAsync(ticket, account, current, failures, cancellationToken);
            Console.WriteLine(current);
            currentReview = await ReviewAsync(ticket, account, current, cancellationToken);
            Report(currentReview);
        }

        ConsoleFormat.Note("""
              Four judgements cost one Jev call over the same state. Asking Claude for the same rubric means
              generating tokens it can then argue with; asking Jev to write the fix is not possible at all.
            """);
    }

    private async Task<string> DraftAsync(string ticket, CancellationToken cancellationToken)
    {
        var response = await claude.GetResponseAsync(
            ticket,
            new ChatOptions
            {
                Instructions = """
                    You are a support agent at Contoso Cloud. Call get_account with the customer's email, then
                    write the reply. Plain text, under 120 words, signed "Contoso Support". Output only the reply.
                    """,
                MaxOutputTokens = 1024,
                Tools = [accounts.GetAccountTool],
            },
            cancellationToken);

        return response.Text.Trim();
    }

    private async Task<string> ReviseAsync(
        string ticket,
        string account,
        string draft,
        IReadOnlyList<string> failures,
        CancellationToken cancellationToken)
    {
        var response = await claude.GetResponseAsync(
            $"""
            Ticket: {ticket}
            Account record: {account}
            Current reply: {draft}
            A reviewer rejected it on: {string.Join(", ", failures)}.
            """,
            new ChatOptions
            {
                Instructions = """
                    Rewrite the reply so it fixes every point the reviewer raised. State only what the account
                    record supports, give a concrete next step, and promise nothing the record does not show.
                    Plain text, under 120 words, signed "Contoso Support". Output only the reply.
                    """,
                MaxOutputTokens = 1024,
            },
            cancellationToken);

        return response.Text.Trim();
    }

    private async Task<SystemOneResponse> ReviewAsync(
        string ticket,
        string account,
        string reply,
        CancellationToken cancellationToken) =>
        await jevClient.SystemOneAsync(
            state: new { ticket, account, reply },
            questions: new Dictionary<string, JevQuestion>
            {
                ["grounded"] = JevQuestion.Noul("Is every factual claim in the reply supported by the account record?"),
                ["actionable"] = JevQuestion.Noul("Does the reply give the customer a concrete next step?"),
                ["no_overpromise"] = JevQuestion.Noul("Does the reply avoid promising a refund, credit or deadline the account record does not support?"),
                ["tone"] = JevQuestion.Score("How empathetic is the reply's tone?", "Cold", "Neutral", "Warm"),
            },
            cancellationToken: cancellationToken);

    private static List<string> Failures(SystemOneResponse review)
    {
        var failures = Requirements
            .Where(key => !review.GetAnswer<JevNoulAnswer>(key).IsYes)
            .ToList();

        if (review.GetAnswer<JevScoreAnswer>("tone").Score < 1)
        {
            failures.Add("tone");
        }

        return failures;
    }

    private static void Report(SystemOneResponse review)
    {
        foreach (var (question, answer) in review.Answers)
        {
            var text = answer switch
            {
                JevNoulAnswer noul => $"{(noul.IsYes ? "pass" : "FAIL")} (p = {noul.Noul:0.00})",
                JevScoreAnswer score => $"{score.Score:0.00} of {score.Legend.Count - 1}, \"{score.Legend.GetValueOrDefault(score.MostLikelyLevel.ToString())}\"",
                _ => answer.Type,
            };
            Console.WriteLine($"  {question,-16} {text}");
        }

        var failures = Failures(review);
        if (failures.Count == 0)
        {
            ConsoleFormat.Good("  -> passes the rubric");
        }
        else
        {
            ConsoleFormat.Bad($"  -> rejected on {string.Join(", ", failures)}");
        }
    }
}
