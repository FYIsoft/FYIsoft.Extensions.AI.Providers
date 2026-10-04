using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Beta.Files;
using FluentAssertions;
using Xunit;

namespace Microsoft.Extensions.AI.Anthropic.Tests;

public class AnthropicCodeExecutionFilesTests
{
    [Fact]
    public async Task ExistingSdk_UploadsAndDownloadsOnlyWhenExplicitlyRequested()
    {
        using var handler = new FileHandler();
        using var http = new HttpClient(handler);
        using var sdk = new AnthropicClient { ApiKey = "test", BaseUrl = "https://test.invalid", HttpClient = http, MaxRetries = 0 };
        using var chat = new AnthropicChatClient(sdk, "claude-test");
        chat.GetService<IAnthropicClient>().Should().BeSameAs(sdk);
        using var csv = new MemoryStream(Encoding.UTF8.GetBytes("value\n1\n2\n3\n"));
        var uploaded = await sdk.Beta.Files.Upload(new FileUploadParams
        {
            File = new BinaryContent { Stream = csv, FileName = "data.csv", ContentType = new MediaTypeHeaderValue("text/csv") }
        });
        var options = ExecutionFixtures.Options();
        ((HostedCodeInterpreterTool)options.Tools![0]).Inputs = [new HostedFileContent(uploaded.ID)];
        var response = await chat.GetResponseAsync(ExecutionFixtures.History(), options);
        var file = response.Messages.Single().Contents.OfType<CodeInterpreterToolResultContent>().Single().Outputs!.OfType<HostedFileContent>().Single();
        handler.Paths.Should().Equal("/v1/files", "/v1/messages");
        var metadata = await sdk.Beta.Files.RetrieveMetadata(file.FileId);
        metadata.Filename.Should().Be("chart.png");
        using var download = await sdk.Beta.Files.Download(file.FileId);
        await using var stream = await download.ReadAsStream();
        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);
        destination.ToArray().Should().Equal(FileHandler.Bytes);
        handler.Paths.Should().Equal("/v1/files", "/v1/messages", "/v1/files/file_chart", "/v1/files/file_chart/content");
        handler.FilesBetaHeaders.Should().OnlyContain(value => value.Contains("files-api"));
    }

    [Theory]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(429)]
    public async Task FileFailures_PreserveSdkStatus(int status)
    {
        using var handler = new FileHandler { Failure = status };
        using var http = new HttpClient(handler);
        using var sdk = new AnthropicClient { ApiKey = "test", BaseUrl = "https://test.invalid", HttpClient = http, MaxRetries = 0 };
        var action = () => sdk.Beta.Files.Download("file_missing");
        var error = await action.Should().ThrowAsync<AnthropicApiException>();
        ((int)error.Which.StatusCode).Should().Be(status);
        handler.Paths.Should().ContainSingle();
    }

    [Fact]
    public void ServiceOnlyConstruction_DoesNotInventAFileClient()
    {
        using var handler = new FakeAnthropicHttpHandler();
        using var sdk = handler.Sdk();
        using var chat = new AnthropicChatClient(sdk.Messages, "claude-test");
        chat.GetService<IAnthropicClient>().Should().BeNull();
    }

    private sealed class FileHandler : HttpMessageHandler
    {
        internal static readonly byte[] Bytes = [137, 80, 78, 71, 13, 10, 26, 10, 0, 255, 127, 42];
        internal List<string> Paths { get; } = [];
        internal List<string> FilesBetaHeaders { get; } = [];
        internal int? Failure { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            Paths.Add(path);
            if (path.Contains("/files")) FilesBetaHeaders.Add(string.Join(',', request.Headers.GetValues("anthropic-beta")));
            if (Failure is { } status) return Task.FromResult(FakeAnthropicHttpHandler.Json(
                """{"type":"error","error":{"type":"api_error","message":"file unavailable"}}""", (HttpStatusCode)status));
            if (path.EndsWith("/content")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Bytes) });
            return Task.FromResult(FakeAnthropicHttpHandler.Json(path == "/v1/messages" ? ExecutionFixtures.Response :
                """{"id":"file_chart","type":"file","filename":"chart.png","mime_type":"image/png","size_bytes":12,"created_at":"2026-10-04T00:00:00Z","downloadable":true}"""));
        }
    }
}
