using Anthropic.Foundry;
using Microsoft.Extensions.DependencyInjection;

namespace Microsoft.Extensions.AI.Anthropic;

/// <summary>
/// Extension methods for registering Azure Anthropic Foundry chat clients with dependency injection.
/// </summary>
/// <remarks>
/// <para>
/// These extensions provide integration with Azure-hosted Anthropic API,
/// supporting multiple authentication methods:
/// <list type="bullet">
/// <item><strong>API Key</strong>: <see cref="AnthropicFoundryApiKeyCredentials"/></item>
/// <item><strong>Default (env-driven)</strong>: <see cref="DefaultAnthropicFoundryCredentials"/></item>
/// <item><strong>Azure Identity</strong>: <see cref="AnthropicFoundryIdentityTokenCredentials"/> (recommended for production)</item>
/// </list>
/// </para>
///
/// <para>
/// <strong>Environment Variables</strong>:
/// <list type="bullet">
/// <item><c>ANTHROPIC_FOUNDRY_RESOURCE</c>: Azure resource name (required)</item>
/// <item><c>ANTHROPIC_FOUNDRY_API_KEY</c>: API key (optional, uses Azure Identity if not set)</item>
/// </list>
/// </para>
/// </remarks>
public static class MicrosoftExtensionsAIAnthropicFoundryExtensions
{
    #region IServiceCollection Extensions

    /// <summary>
    /// Adds an Azure Anthropic Foundry chat client to the service collection from environment variables.
    /// </summary>
    /// <param name="services">The service collection to add the client to.</param>
    /// <param name="resourceName">
    /// Optional Azure resource name. If not specified, reads from ANTHROPIC_FOUNDRY_RESOURCE environment variable.
    /// </param>
    /// <param name="modelId">
    /// Optional default model ID (e.g., "claude-sonnet-4-5"). If not specified, must be provided in each request.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="services"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// Thrown when environment variables are not configured or credentials cannot be created.
    /// </exception>
    public static IServiceCollection AddAnthropicFoundryChatClientFromEnvironment(
        this IServiceCollection services,
        string? resourceName = null,
        string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IChatClient>(sp =>
        {
            var credentials = IAnthropicFoundryCredentials.FromEnv();
            if (credentials is null)
            {
                throw new InvalidOperationException(
                    "Failed to create Azure Foundry credentials from environment. " +
                    "Ensure ANTHROPIC_FOUNDRY_RESOURCE is set.");
            }

            var foundryClient = new AnthropicFoundryClient(credentials);
            return new AnthropicChatClient(foundryClient, modelId);
        });

        return services;
    }

    /// <summary>
    /// Adds an Azure Anthropic Foundry chat client to the service collection with explicit credentials.
    /// </summary>
    /// <param name="services">The service collection to add the client to.</param>
    /// <param name="credentials">The Azure Foundry credentials to use.</param>
    /// <param name="modelId">
    /// Optional default model ID (e.g., "claude-sonnet-4-5"). If not specified, must be provided in each request.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="credentials"/> is null.
    /// </exception>
    public static IServiceCollection AddAnthropicFoundryChatClient(
        this IServiceCollection services,
        IAnthropicFoundryCredentials credentials,
        string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(credentials);

        services.AddSingleton<IChatClient>(sp =>
        {
            var foundryClient = new AnthropicFoundryClient(credentials);
            return new AnthropicChatClient(foundryClient, modelId);
        });

        return services;
    }

    /// <summary>
    /// Adds an Azure Anthropic Foundry chat client to the service collection with resource name and API key.
    /// </summary>
    /// <param name="services">The service collection to add the client to.</param>
    /// <param name="resourceName">The Azure resource name.</param>
    /// <param name="apiKey">The API key for authentication.</param>
    /// <param name="modelId">
    /// Optional default model ID (e.g., "claude-sonnet-4-5"). If not specified, must be provided in each request.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when any required parameter is null or empty.
    /// </exception>
    public static IServiceCollection AddAnthropicFoundryChatClient(
        this IServiceCollection services,
        string resourceName,
        string apiKey,
        string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (string.IsNullOrWhiteSpace(resourceName))
        {
            throw new ArgumentException("Resource name cannot be null or empty.", nameof(resourceName));
        }
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API key cannot be null or empty.", nameof(apiKey));
        }

        var credentials = new AnthropicFoundryApiKeyCredentials(
            apiKey: apiKey,
            resourceName: resourceName);

        return services.AddAnthropicFoundryChatClient(credentials, modelId);
    }

    /// <summary>
    /// Adds an Azure Anthropic Foundry chat client to the service collection using a factory function.
    /// </summary>
    /// <param name="services">The service collection to add the client to.</param>
    /// <param name="clientFactory">
    /// A factory function that creates the <see cref="AnthropicFoundryClient"/> instance.
    /// </param>
    /// <param name="modelId">
    /// Optional default model ID (e.g., "claude-sonnet-4-5"). If not specified, must be provided in each request.
    /// </param>
    /// <returns>The service collection for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="services"/> or <paramref name="clientFactory"/> is null.
    /// </exception>
    public static IServiceCollection AddAnthropicFoundryChatClient(
        this IServiceCollection services,
        Func<IServiceProvider, AnthropicFoundryClient> clientFactory,
        string? modelId = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clientFactory);

        services.AddSingleton<IChatClient>(sp =>
        {
            var foundryClient = clientFactory(sp);
            return new AnthropicChatClient(foundryClient, modelId);
        });

        return services;
    }

    #endregion

    // Note: IChatClientBuilder extensions have been removed as IChatClientBuilder does not exist
    // in Microsoft.Extensions.AI.Abstractions. Use the IServiceCollection extensions above for DI registration.
}
