using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Jev;
using Microsoft.Extensions.DependencyInjection;

namespace SupportDeskSample.Demos;

/// <summary>
/// The other half of the picture: what Jev cannot be asked for at all. Every case here is expected to fail,
/// and most fail before a request is ever sent.
/// </summary>
public sealed class LimitsDemo([FromKeyedServices(SupportDesk.JevKey)] IChatClient jev, JevClient jevClient)
{
    /// <summary>Jev has no way to emit a string, so extraction cannot be expressed.</summary>
    private sealed record OrderLookup(
        [property: Description("The order number mentioned in the ticket.")] string OrderId);

    /// <summary>Numbers are not a question type either; only bool, enum and explicit score questions.</summary>
    private sealed record Severity(
        [property: Description("Severity from 1 to 5.")] int Level);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ConsoleFormat.Heading("What Jev cannot do");

        const string ticket = "Order A-4821 arrived damaged, I want a replacement. - dana@example.com";

        await ExpectFailure(
            "Extract the order number (free-form string output)",
            () => jev.GetResponseAsync<OrderLookup>(ticket, cancellationToken: cancellationToken));

        await ExpectFailure(
            "Return a numeric severity (int property)",
            () => jev.GetResponseAsync<Severity>(ticket, cancellationToken: cancellationToken));

        // Questions are supplied here, so the failure is about the image itself and not a missing question.
        await ExpectFailure(
            "Judge an image attachment",
            () => jev.GetResponseAsync(
                new ChatMessage(ChatRole.User, [new DataContent(new byte[] { 1, 2, 3 }, "image/png")]),
                new ChatOptions().WithJevQuestions(new Dictionary<string, JevQuestion>
                {
                    ["damaged"] = JevQuestion.Noul("Does the photo show damage?"),
                }),
                cancellationToken));

        await ExpectFailure(
            "Answer without questions (open-ended prompt)",
            () => jev.GetResponseAsync(ticket, cancellationToken: cancellationToken));

        await ExpectFailure(
            "Choose between 300 labels",
            () => jevClient.SystemOneAsync(
                ticket,
                new Dictionary<string, JevQuestion>
                {
                    ["area"] = JevQuestion.Choice("Which product area?", Enumerable.Range(1, 300).Select(i => $"area-{i}")),
                },
                cancellationToken: cancellationToken));

        await ExpectFailure(
            "Score on a single level",
            () => jevClient.SystemOneAsync(
                ticket,
                new Dictionary<string, JevQuestion>
                {
                    ["severity"] = JevQuestion.Score("How severe?", "Bad"),
                },
                cancellationToken: cancellationToken));

        await ToolsAndStreaming(ticket, cancellationToken);

        ConsoleFormat.Note("""
              None of this is a bug: Jev is a judgement model, not a generative one. Anything that has to
              produce words, call a tool, read an image or stream belongs to Claude.
            """);
    }

    /// <summary>Tools are dropped and streaming yields one update, both silently rather than as an error.</summary>
    private async Task ToolsAndStreaming(string ticket, CancellationToken cancellationToken)
    {
        var toolCalled = false;
        var options = new ChatOptions
        {
            Tools = [AIFunctionFactory.Create(() => { toolCalled = true; return "never reached"; }, name: "lookup_order")],
        }.WithJevQuestions(new Dictionary<string, JevQuestion>
        {
            ["damaged"] = JevQuestion.Noul("Does the customer report physical damage?"),
        });

        var updates = 0;
        var textChunks = 0;
        var text = string.Empty;
        await foreach (var update in jev.GetStreamingResponseAsync(ticket, options, cancellationToken))
        {
            updates++;
            if (!string.IsNullOrEmpty(update.Text))
            {
                textChunks++;
                text += update.Text;
            }
        }

        Console.WriteLine();
        ConsoleFormat.Write("Tools and streaming (no exception, just ignored)", ConsoleColor.Yellow);
        Console.WriteLine($"  tool invoked:     {toolCalled} (ChatOptions.Tools is dropped)");
        Console.WriteLine($"  stream updates:   {updates} ({textChunks} carrying text, the rest usage)");
        Console.WriteLine("                    the call completes before the first update; nothing arrives incrementally");
        Console.WriteLine($"  response text:    {text}");
        Console.WriteLine("  -> the 'text' is the answers JSON. Jev never writes prose.");
    }

    private static async Task ExpectFailure(string what, Func<Task> action)
    {
        Console.WriteLine();
        ConsoleFormat.Write(what, ConsoleColor.Yellow);
        try
        {
            await action();
            ConsoleFormat.Bad("  -> no failure, which this demo did not expect");
        }
        catch (Exception ex)
        {
            ConsoleFormat.Good($"  -> {ex.GetType().Name}");
            Console.WriteLine($"     {ex.Message}");
        }
    }
}
