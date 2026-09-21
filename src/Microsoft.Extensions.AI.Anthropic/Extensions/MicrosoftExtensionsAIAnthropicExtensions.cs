using Anthropic;
using Anthropic.Core;
using Microsoft.Extensions.DependencyInjection;
// ReSharper disable CheckNamespace

namespace Microsoft.Extensions.AI.Anthropic;

/// <summary>
/// Extension methods for registering standard Anthropic chat clients with dependency injection.
/// </summary>
/// <remarks>
/// <para>
/// These extensions provide integration with the standard Anthropic API (api.anthropic.com).
/// For Azure-hosted Anthropic API, see <see cref="MicrosoftExtensionsAIAnthropicFoundryExtensions"/>.
/// </para>
///
/// <para>
/// <strong>Authentication</strong>:
/// Requires an Anthropic API key (starting with "sk-ant-"). Set via:
/// <list type="bullet">
/// <item><c>ANTHROPIC_API_KEY</c> environment variable, or</item>
/// <item>Explicit parameter in extension methods</item>
/// </list>
/// </para>
/// </remarks>
public static class MicrosoftExtensionsAIAnthropicExtensions
{
    #region IServiceCollection Extensions

    /// <summary>
    /// Adds a standard Anthropic chat client to the service collection with an API key.
    /// </summary>
    /// <param name="services">The service collection to add the client to.</param>
    /// <param name="apiKey">
    /// The Anthropic API key (starts with "sk-ant-"). If null, reads from ANTHROPIC_API_KEY environment variable.
    /// </param>
    /// <param name="modelId">
    /// Optional default model ID (e.g., "claude-sonnet-4-5"). If not specified, must be provided in each request.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown when API key is not provided or invalid.</exception>
    public static IServiceCollection AddAnthropicChatClient(
        this IServiceCollection services,
        string? apiKey = null,
        string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var effectiveApiKey = apiKey ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
        if (string.IsNullOrWhiteSpace(effectiveApiKey))
        {
            throw new ArgumentException(
                "API key must be provided either as parameter or via ANTHROPIC_API_KEY environment variable.",
                nameof(apiKey));
        }

        services.AddSingleton<IChatClient>(sp =>
        {
            var anthropicClient = new AnthropicClient(new ClientOptions
            {
                ApiKey = effectiveApiKey
            });
            return new AnthropicChatClient(anthropicClient, modelId);
        });

        return services;
    }

    /// <summary>
    /// Adds a standard Anthropic chat client to the service collection with explicit client options.
    /// </summary>
    /// <param name="services">The service collection to add the client to.</param>
    /// <param name="clientOptions">The Anthropic client options including API key and base URL.</param>
    /// <param name="modelId">
    /// Optional default model ID (e.g., "claude-sonnet-4-5"). If not specified, must be provided in each request.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> is null.
    /// </exception>
    public static IServiceCollection AddAnthropicChatClient(
        this IServiceCollection services,
        ClientOptions clientOptions,
        string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IChatClient>(sp =>
        {
            var anthropicClient = new AnthropicClient(clientOptions);
            return new AnthropicChatClient(anthropicClient, modelId);
        });

        return services;
    }

    /// <summary>
    /// Adds a standard Anthropic chat client to the service collection with an explicit client instance.
    /// </summary>
    /// <param name="services">The service collection to add the client to.</param>
    /// <param name="anthropicClient">The Anthropic client instance to use.</param>
    /// <param name="modelId">
    /// Optional default model ID (e.g., "claude-sonnet-4-5"). If not specified, must be provided in each request.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="anthropicClient"/> is null.
    /// </exception>
    public static IServiceCollection AddAnthropicChatClient(
        this IServiceCollection services,
        IAnthropicClient anthropicClient,
        string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(anthropicClient);

        services.AddSingleton<IChatClient>(sp => new AnthropicChatClient(anthropicClient, modelId));

        return services;
    }

    /// <summary>
    /// Adds a standard Anthropic chat client to the service collection using a factory function.
    /// </summary>
    /// <param name="services">The service collection to add the client to.</param>
    /// <param name="clientFactory">
    /// A factory function that creates the <see cref="IAnthropicClient"/> instance.
    /// Can be used to access IConfiguration or other services.
    /// </param>
    /// <param name="modelId">
    /// Optional default model ID (e.g., "claude-sonnet-4-5"). If not specified, must be provided in each request.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="clientFactory"/> is null.
    /// </exception>
    public static IServiceCollection AddAnthropicChatClient(
        this IServiceCollection services,
        Func<IServiceProvider, IAnthropicClient> clientFactory,
        string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clientFactory);

        services.AddSingleton<IChatClient>(sp =>
        {
            var anthropicClient = clientFactory(sp);
            return new AnthropicChatClient(anthropicClient, modelId);
        });

        return services;
    }

    #endregion

    // Note: IChatClientBuilder extensions have been removed as IChatClientBuilder does not exist
    // in Microsoft.Extensions.AI.Abstractions. Use the IServiceCollection extensions above for DI registration.
}
