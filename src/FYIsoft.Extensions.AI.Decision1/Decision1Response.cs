namespace Microsoft.Extensions.AI.Decision1;

/// <summary>The result of one Microsoft-Decision-1 request.</summary>
public sealed class Decision1Response
{
    internal Decision1Response(string model, IReadOnlyDictionary<string, Decision1Answer> answers, Decision1Usage usage, Decision1ResponseMetadata metadata)
    {
        Model = model;
        Answers = answers;
        Usage = usage;
        Metadata = metadata;
    }

    /// <summary>The underlying model, for example <c>microsoft-decision-1</c>. This is not the deployment name.</summary>
    public string Model { get; }

    /// <summary>The answers keyed by question name, in the order the service returned them.</summary>
    public IReadOnlyDictionary<string, Decision1Answer> Answers { get; }

    /// <summary>Token usage for the request.</summary>
    public Decision1Usage Usage { get; }

    /// <summary>Request id, region and rate-limit headers from the response.</summary>
    public Decision1ResponseMetadata Metadata { get; }

    /// <summary>Returns the answer with the given name as the expected type.</summary>
    /// <typeparam name="T"><see cref="Decision1NoulAnswer"/>, <see cref="Decision1ChoiceAnswer"/> or <see cref="Decision1ScoreAnswer"/>.</typeparam>
    /// <exception cref="KeyNotFoundException">No answer has that name.</exception>
    /// <exception cref="InvalidCastException">The answer has a different type.</exception>
    public T GetAnswer<T>(string name) where T : Decision1Answer
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!Answers.TryGetValue(name, out var answer))
            throw new KeyNotFoundException(
                $"The response has no answer named '{name}'. Answers: {(Answers.Count == 0 ? "(none)" : string.Join(", ", Answers.Keys))}.");
        return answer as T ?? throw new InvalidCastException(
            $"Answer '{name}' is a {answer.Type} answer ({answer.GetType().Name}), not {typeof(T).Name}.");
    }

    /// <summary>Tries to return the answer with the given name as the expected type.</summary>
    /// <returns>True when an answer with that name and type exists.</returns>
    public bool TryGetAnswer<T>(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out T? answer) where T : Decision1Answer
    {
        ArgumentNullException.ThrowIfNull(name);
        answer = Answers.TryGetValue(name, out var found) ? found as T : null;
        return answer is not null;
    }
}

/// <summary>Token usage reported by the service.</summary>
/// <param name="InputTokens">Input tokens (<c>usage.input_tokens</c>).</param>
/// <param name="OutputTokens">Output tokens (<c>usage.output_tokens</c>).</param>
public sealed record Decision1Usage(int InputTokens, int OutputTokens)
{
    /// <summary>Input plus output tokens.</summary>
    public int TotalTokens => InputTokens + OutputTokens;
}

/// <summary>Response headers worth logging or acting on. Every value is null when the header is absent.</summary>
public sealed class Decision1ResponseMetadata
{
    /// <summary>HTTP status code of the final attempt.</summary>
    public int StatusCode { get; init; }

    /// <summary>The service request id (<c>apim-request-id</c>, else <c>x-request-id</c>, else <c>x-ms-client-request-id</c>).</summary>
    public string? RequestId { get; init; }

    /// <summary>The serving region (<c>x-ms-region</c>), for example "East US 2".</summary>
    public string? Region { get; init; }

    /// <summary>Requests allowed per renewal period (<c>x-ratelimit-limit-requests</c>).</summary>
    public int? RateLimitLimitRequests { get; init; }

    /// <summary>Requests left in the current period (<c>x-ratelimit-remaining-requests</c>).</summary>
    public int? RateLimitRemainingRequests { get; init; }

    /// <summary>Time until the request limit resets (<c>x-ratelimit-reset-requests</c>, sent in seconds).</summary>
    public TimeSpan? RateLimitResetRequests { get; init; }

    /// <summary>Length of the rate-limit period (<c>x-ratelimit-renewalperiod-requests</c>, sent in seconds).</summary>
    public TimeSpan? RateLimitRenewalPeriodRequests { get; init; }

    /// <summary>How many HTTP attempts the client made, including the final one.</summary>
    public int Attempts { get; init; }
}
