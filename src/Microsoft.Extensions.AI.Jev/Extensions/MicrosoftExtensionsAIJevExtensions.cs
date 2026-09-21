using Microsoft.Extensions.AI;
using Microsoft.Extensions.AI.Jev;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

// ReSharper disable CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Dependency injection registration for <see cref="JevClient"/> and <see cref="JevChatClient"/>.
/// </summary>
/// <remarks>
/// Requires an API key via <see cref="JevClientOptions.ApiKey"/> or the <c>TYPESAFE_API_KEY</c> environment variable.
/// </remarks>
public static class MicrosoftExtensionsAIJevExtensions
{
    /// <summary>
    /// Registers a singleton <see cref="JevClient"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options callback; unset values come from environment variables.</param>
    public static IServiceCollection AddJevClient(
        this IServiceCollection services,
        Action<JevClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(sp =>
        {
            var options = new JevClientOptions();
            configure?.Invoke(options);
            var logger = sp.GetService<ILoggerFactory>()?.CreateLogger<JevClient>();
            return new JevClient(options, logger: logger);
        });

        return services;
    }

    /// <summary>
    /// Registers a singleton <see cref="JevClient"/> built by a factory (e.g. to supply an <see cref="HttpClient"/>).
    /// </summary>
    public static IServiceCollection AddJevClient(
        this IServiceCollection services,
        Func<IServiceProvider, JevClient> clientFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(clientFactory);

        services.TryAddSingleton(clientFactory);
        return services;
    }

    /// <summary>
    /// Registers a singleton <see cref="JevClient"/> and an <see cref="IChatClient"/> adapter over it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options callback; unset values come from environment variables.</param>
    /// <param name="modelId">Optional default model; falls back to <see cref="JevClientOptions.DefaultModel"/>.</param>
    public static IServiceCollection AddJevChatClient(
        this IServiceCollection services,
        Action<JevClientOptions>? configure = null,
        string? modelId = null)
    {
        services.AddJevClient(configure);
        services.AddSingleton<IChatClient>(sp => new JevChatClient(sp.GetRequiredService<JevClient>(), modelId));
        return services;
    }

    /// <summary>
    /// Wraps a <see cref="JevClient"/> as an <see cref="IChatClient"/>. Disposing the adapter does not dispose the client.
    /// </summary>
    public static IChatClient AsIChatClient(this JevClient client, string? modelId = null) =>
        new JevChatClient(client, modelId);
}
