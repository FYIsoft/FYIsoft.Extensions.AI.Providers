using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Microsoft.Extensions.AI.Decision1.Tests;

public class Decision1DependencyInjectionTests
{
    [Fact]
    public async Task AddDecision1Client_RegistersATypedHttpClient()
    {
        var handler = new FakeDecision1Handler(ProbeFixtures.StringStateNoulApikey);
        var services = new ServiceCollection();
        services.AddDecision1Client(o =>
            {
                o.Endpoint = new Uri(TestClient.Endpoint);
                o.Deployment = TestClient.Deployment;
                o.ApiKey = TestClient.ApiKey;
                o.UseEnvironmentVariables = false;
            })
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredService<IDecision1Client>();
        var answer = await client.DecideAsync("I was charged twice for October.", Decision1Question.Noul("Billing?"), "billing");

        client.Should().BeOfType<Decision1Client>().Which.Deployment.Should().Be(TestClient.Deployment);
        answer.Noul.Should().Be(0.9980732633607365);
        handler.Last.Headers["api-key"].Should().Be(TestClient.ApiKey);
        handler.Last.Uri.AbsoluteUri.Should().Be(TestClient.Endpoint + "/providers/microsoft/v1/systemone");
    }

    [Fact]
    public async Task ResolvedClients_ShareOneTokenCache()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var credential = new FakeTokenCredential(time, TimeSpan.FromHours(1));
        var handler = new FakeDecision1Handler(ProbeFixtures.CombinedEntraServices);
        var services = new ServiceCollection();
        services.AddDecision1Client(o =>
            {
                o.Endpoint = new Uri(TestClient.Endpoint);
                o.Deployment = TestClient.Deployment;
                o.Credential = credential;
                o.UseEnvironmentVariables = false;
            })
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IDecision1Client>();
        var second = provider.GetRequiredService<IDecision1Client>();
        await first.DecideAsync("x", Questions.SingleNoul());
        await second.DecideAsync("x", Questions.SingleNoul());

        first.Should().NotBeSameAs(second, "typed HttpClient clients are transient");
        credential.Calls.Should().Be(1);
    }

    [Fact]
    public async Task AddDecision1Client_BindsConfiguration_ThenAppliesCodeConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Decision1:Endpoint"] = "https://configured.cognitiveservices.azure.com/",
            ["Decision1:Deployment"] = "configured-deployment",
            ["Decision1:Timeout"] = "00:00:03",
            ["Decision1:Retry:MaxRetries"] = "4",
            ["Decision1:UseEnvironmentVariables"] = "false",
        }).Build();
        var handler = new FakeDecision1Handler(ProbeFixtures.StringStateNoulApikey);
        var services = new ServiceCollection();
        services.AddDecision1Client(configuration.GetSection(Decision1ClientOptions.SectionName), o => o.ApiKey = TestClient.ApiKey)
            .ConfigurePrimaryHttpMessageHandler(() => handler);
        using var provider = services.BuildServiceProvider();

        var client = (Decision1Client)provider.GetRequiredService<IDecision1Client>();
        await client.DecideAsync("x", Questions.SingleNoul());

        client.RequestUri.AbsoluteUri.Should().Be("https://configured.cognitiveservices.azure.com/providers/microsoft/v1/systemone");
        handler.Last.Json.GetProperty("model").GetString().Should().Be("configured-deployment");
        var core = provider.GetRequiredService<Decision1ClientCore>();
        core.Options.Timeout.Should().Be(TimeSpan.FromSeconds(3));
        core.Options.Retry.MaxRetries.Should().Be(4);
    }

    [Fact]
    public void InvalidOptions_FailWhenTheClientIsResolved()
    {
        var services = new ServiceCollection();
        services.AddDecision1Client(o => o.UseEnvironmentVariables = false);
        using var provider = services.BuildServiceProvider();

        FluentActions.Invoking(() => provider.GetRequiredService<IDecision1Client>())
            .Should().Throw<ArgumentException>().WithMessage("*Endpoint is required*");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvironmentVariableCollection
{
    public const string Name = "Decision1 environment variables";
}

/// <summary>Sets process environment variables, so these tests run alone and restore previous values.</summary>
[Collection(EnvironmentVariableCollection.Name)]
public class Decision1EnvironmentFallbackTests
{
    private static readonly string[] Names =
    [
        Decision1ClientOptions.EndpointEnvironmentVariable,
        Decision1ClientOptions.DeploymentEnvironmentVariable,
        Decision1ClientOptions.ApiKeyEnvironmentVariable,
    ];

    private static void WithEnvironment(Dictionary<string, string?> values, Action action)
    {
        var saved = Names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in Names)
                Environment.SetEnvironmentVariable(name, values.GetValueOrDefault(name));
            action();
        }
        finally
        {
            foreach (var (name, value) in saved)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    [Fact]
    public void FromEnvironment_ReadsEndpointDeploymentAndKey() => WithEnvironment(new()
    {
        ["DECISION1_ENDPOINT"] = "https://env-resource.services.ai.azure.com",
        ["DECISION1_DEPLOYMENT"] = "env-deployment",
        ["DECISION1_API_KEY"] = "synthetic-env-key",
    }, () =>
    {
        var resolved = Decision1ClientOptions.FromEnvironment().Resolve();

        resolved.RequestUri.AbsoluteUri.Should().Be("https://env-resource.services.ai.azure.com/providers/microsoft/v1/systemone");
        resolved.Deployment.Should().Be("env-deployment");
        resolved.ApiKey.Should().Be("synthetic-env-key");
        resolved.Credential.Should().BeNull();
    });

    [Fact]
    public void ExplicitValues_WinOverEnvironment_AndCredentialSuppressesTheEnvironmentKey() => WithEnvironment(new()
    {
        ["DECISION1_ENDPOINT"] = "https://env-resource.services.ai.azure.com",
        ["DECISION1_DEPLOYMENT"] = "env-deployment",
        ["DECISION1_API_KEY"] = "synthetic-env-key",
    }, () =>
    {
        var credential = new FakeTokenCredential(TimeProvider.System, TimeSpan.FromHours(1));
        var resolved = new Decision1ClientOptions { Deployment = "explicit", Credential = credential }.Resolve();

        resolved.Deployment.Should().Be("explicit");
        resolved.ApiKey.Should().BeNull("a configured credential means the environment key is not used");
        resolved.Credential.Should().BeSameAs(credential);
    });

    [Fact]
    public void UseEnvironmentVariablesFalse_IgnoresTheEnvironment() => WithEnvironment(new()
    {
        ["DECISION1_ENDPOINT"] = "https://env-resource.services.ai.azure.com",
    }, () =>
    {
        FluentActions.Invoking(() => new Decision1ClientOptions { UseEnvironmentVariables = false }.Resolve())
            .Should().Throw<ArgumentException>().WithMessage("*Endpoint is required*");
    });

    [Fact]
    public void InvalidEnvironmentEndpoint_IsReported() => WithEnvironment(new()
    {
        ["DECISION1_ENDPOINT"] = "not a uri",
    }, () =>
    {
        FluentActions.Invoking(() => Decision1ClientOptions.FromEnvironment().Resolve())
            .Should().Throw<ArgumentException>().WithMessage("*DECISION1_ENDPOINT is not an absolute URI*");
    });
}
