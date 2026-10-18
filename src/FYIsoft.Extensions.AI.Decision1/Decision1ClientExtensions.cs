namespace Microsoft.Extensions.AI.Decision1;

/// <summary>Single-question convenience overloads for <see cref="IDecision1Client"/>.</summary>
/// <remarks>
/// Each overload sends one request with one question and returns only the typed answer. Use
/// <see cref="IDecision1Client.DecideAsync(Decision1State, IEnumerable{KeyValuePair{string, Decision1Question}}, CancellationToken)"/>
/// to combine questions in one request or to read usage and response metadata.
/// </remarks>
public static class Decision1ClientExtensions
{
    /// <summary>The question name the single-question overloads use when none is given.</summary>
    public const string DefaultQuestionName = "answer";

    /// <summary>Asks one true-or-false question.</summary>
    /// <param name="client">The client.</param>
    /// <param name="state">The state to judge.</param>
    /// <param name="question">The question, from <see cref="Decision1Question.Noul(string)"/>.</param>
    /// <param name="name">The question name sent to the service. Defaults to <see cref="DefaultQuestionName"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task<Decision1NoulAnswer> DecideAsync(this IDecision1Client client, Decision1State state, Decision1NoulQuestion question, string name = DefaultQuestionName, CancellationToken cancellationToken = default) =>
        DecideOneAsync<Decision1NoulAnswer>(client, state, question, name, cancellationToken);

    /// <summary>Asks one choice question.</summary>
    /// <param name="client">The client.</param>
    /// <param name="state">The state to judge.</param>
    /// <param name="question">The question, from <see cref="Decision1Question.Choice(string, IEnumerable{KeyValuePair{string, string}})"/>.</param>
    /// <param name="name">The question name sent to the service. Defaults to <see cref="DefaultQuestionName"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task<Decision1ChoiceAnswer> DecideAsync(this IDecision1Client client, Decision1State state, Decision1ChoiceQuestion question, string name = DefaultQuestionName, CancellationToken cancellationToken = default) =>
        DecideOneAsync<Decision1ChoiceAnswer>(client, state, question, name, cancellationToken);

    /// <summary>Asks one score question.</summary>
    /// <param name="client">The client.</param>
    /// <param name="state">The state to judge.</param>
    /// <param name="question">The question, from <see cref="Decision1Question.Score(string, IEnumerable{string})"/>.</param>
    /// <param name="name">The question name sent to the service. Defaults to <see cref="DefaultQuestionName"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public static Task<Decision1ScoreAnswer> DecideAsync(this IDecision1Client client, Decision1State state, Decision1ScoreQuestion question, string name = DefaultQuestionName, CancellationToken cancellationToken = default) =>
        DecideOneAsync<Decision1ScoreAnswer>(client, state, question, name, cancellationToken);

    private static Task<T> DecideOneAsync<T>(IDecision1Client client, Decision1State state, Decision1Question question, string name, CancellationToken cancellationToken)
        where T : Decision1Answer
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(question);
        // DecideAsync validates synchronously, so argument errors surface before the first await.
        var pending = client.DecideAsync(state, [new KeyValuePair<string, Decision1Question>(name, question)], cancellationToken);
        return Unwrap(pending, name);

        static async Task<T> Unwrap(Task<Decision1Response> pending, string name) =>
            (await pending.ConfigureAwait(false)).GetAnswer<T>(name);
    }
}
