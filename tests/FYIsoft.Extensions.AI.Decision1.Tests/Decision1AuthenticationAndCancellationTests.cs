using FluentAssertions;
using Xunit;

namespace Microsoft.Extensions.AI.Decision1.Tests;

public class Decision1AuthenticationAndCancellationTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-18T12:00:00Z");

    private static (TestClient Test, FakeTokenCredential Credential, ManualTimeProvider Time) EntraClient(params ProbeFixture[] script)
    {
        var time = new ManualTimeProvider(Start);
        var credential = new FakeTokenCredential(time, TimeSpan.FromHours(1));
        var test = TestClient.Create(new FakeDecision1Handler(script.Length == 0 ? [ProbeFixtures.CombinedEntraServices] : script),
            o => { o.ApiKey = null; o.Credential = credential; }, time);
        return (test, credential, time);
    }

    [Fact]
    public async Task Token_IsCachedAcrossRequests()
    {
        var (test, credential, _) = EntraClient();
        using var _test = test;

        for (var i = 0; i < 3; i++)
            await test.Client.DecideAsync("x", Questions.SingleNoul());

        credential.Calls.Should().Be(1);
        test.Handler.Requests.Select(r => r.Headers["Authorization"]).Should().AllBe("Bearer synthetic-token-1");
    }

    [Fact]
    public async Task Token_IsRefreshedFiveMinutesBeforeExpiry()
    {
        var (test, credential, time) = EntraClient();
        using var _test = test;

        await test.Client.DecideAsync("x", Questions.SingleNoul());
        time.Advance(TimeSpan.FromMinutes(54));
        await test.Client.DecideAsync("x", Questions.SingleNoul());
        credential.Calls.Should().Be(1, "54 minutes into a 60-minute token is outside the 5-minute refresh margin");

        time.Advance(TimeSpan.FromMinutes(2));
        await test.Client.DecideAsync("x", Questions.SingleNoul());

        credential.Calls.Should().Be(2);
        test.Handler.Last.Headers["Authorization"].Should().Be("Bearer synthetic-token-2");
    }

    [Fact]
    public async Task Token_ConcurrentRequestsShareOneAcquisition()
    {
        var (test, credential, _) = EntraClient();
        using var _test = test;

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => test.Client.DecideAsync("x", Questions.SingleNoul())));

        credential.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Unauthorized_DropsTheCachedToken_SoTheNextCallRefreshes()
    {
        var (test, credential, _) = EntraClient(ProbeFixtures.NoAuth401, ProbeFixtures.CombinedEntraServices);
        using var _test = test;

        await FluentActions.Invoking(() => test.Client.DecideAsync("x", Questions.SingleNoul())).Should().ThrowAsync<Decision1AuthenticationException>();
        await test.Client.DecideAsync("x", Questions.SingleNoul());

        credential.Calls.Should().Be(2);
        test.Handler.Requests.Should().HaveCount(2, "a 401 is not retried");
    }

    [Fact]
    public async Task Retries_ReuseTheCachedToken()
    {
        var (test, credential, _) = EntraClient(ProbeFixtures.Synthetic503, ProbeFixtures.CombinedEntraServices);
        using var _test = test;

        await test.Client.DecideAsync("x", Questions.SingleNoul());

        credential.Calls.Should().Be(1);
        test.Handler.Requests.Should().HaveCount(2);
    }

    [Fact]
    public async Task AlreadyCanceledToken_ThrowsOperationCanceled_WithoutSending()
    {
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.StringStateNoulApikey));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => test.Client.DecideAsync("x", Questions.SingleNoul(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        test.Handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelDuringRequest_ThrowsOperationCanceled_NotTimeoutOrRetry()
    {
        using var cts = new CancellationTokenSource();
        using var test = TestClient.Create(new FakeDecision1Handler(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }));

        var act = () => test.Client.DecideAsync("x", Questions.SingleNoul(), cts.Token);

        var ex = await act.Should().ThrowAsync<OperationCanceledException>();
        ex.Which.Should().NotBeAssignableTo<Decision1Exception>();
        test.Handler.Requests.Should().HaveCount(1);
        test.Delays.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelDuringRetryDelay_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        using var test = TestClient.Create(new FakeDecision1Handler(ProbeFixtures.Synthetic503));
        test.Core.DelayAsync = async (delay, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(delay + TimeSpan.FromSeconds(30), ct);
        };

        var act = () => test.Client.DecideAsync("x", Questions.SingleNoul(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        test.Handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task CancelDuringTokenAcquisition_ThrowsOperationCanceled()
    {
        var (test, _, _) = EntraClient();
        using var _test = test;
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await FluentActions.Invoking(() => test.Client.DecideAsync("x", Questions.SingleNoul(), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        test.Handler.Requests.Should().BeEmpty();
    }
}
