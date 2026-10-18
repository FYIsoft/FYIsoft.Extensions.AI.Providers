using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Microsoft.Extensions.AI.Decision1;

/// <summary>
/// Registers <see cref="IDecision1Client"/> as a typed <see cref="HttpClient"/> client through
/// <c>IHttpClientFactory</c>.
/// </summary>
/// <remarks>
/// <para>
/// Options come from <see cref="Decision1ClientOptions"/> (named options are not used). Null endpoint,
/// deployment and key settings fall back to <c>DECISION1_ENDPOINT</c>, <c>DECISION1_DEPLOYMENT</c> and
/// <c>DECISION1_API_KEY</c>. Options are resolved and validated on first use of the client.
/// </para>
/// <para>
/// The client already retries transient failures. Do not add a second retry handler to the returned
/// <see cref="IHttpClientBuilder"/>, or set <see cref="Decision1RetryOptions.MaxRetries"/> to 0 if you do.
/// </para>
/// </remarks>
public static class Decision1ServiceCollectionExtensions
{
    /// <summary>The name of the <see cref="HttpClient"/> the client uses.</summary>
    public const string HttpClientName = "Decision1";

    /// <summary>Registers the client, configured in code and/or from environment variables.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets endpoint, deployment, credential or key, timeout and retries.</param>
    /// <returns>The HTTP client builder, for handler or primary-handler customization.</returns>
    public static IHttpClientBuilder AddDecision1Client(this IServiceCollection services, Action<Decision1ClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var optionsBuilder = services.AddOptions<Decision1ClientOptions>();
        if (configure is not null)
            optionsBuilder.Configure(configure);
        return AddCore(services);
    }

    /// <summary>
    /// Registers the client, binding <see cref="Decision1ClientOptions"/> from configuration (for example
    /// the <c>Decision1</c> section), then applying <paramref name="configure"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The section to bind: <c>Endpoint</c>, <c>Deployment</c>, <c>ApiKey</c>, <c>Timeout</c>, <c>Retry:*</c>.</param>
    /// <param name="configure">Optional code configuration, for example to set <see cref="Decision1ClientOptions.Credential"/>.</param>
    /// <returns>The HTTP client builder.</returns>
    public static IHttpClientBuilder AddDecision1Client(this IServiceCollection services, IConfiguration configuration, Action<Decision1ClientOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        var optionsBuilder = services.AddOptions<Decision1ClientOptions>().Bind(configuration);
        if (configure is not null)
            optionsBuilder.Configure(configure);
        return AddCore(services);
    }

    private static IHttpClientBuilder AddCore(IServiceCollection services)
    {
        // One core per provider: typed clients are transient, but the token cache must be shared.
        services.AddSingleton(sp => Decision1ClientCore.Create(sp.GetRequiredService<IOptions<Decision1ClientOptions>>().Value));

        return services.AddHttpClient<IDecision1Client, Decision1Client>(HttpClientName, (httpClient, sp) =>
            new Decision1Client(
                httpClient,
                sp.GetRequiredService<Decision1ClientCore>(),
                sp.GetService<ILoggerFactory>()?.CreateLogger<Decision1Client>(),
                disposeHttpClient: false));
    }
}
