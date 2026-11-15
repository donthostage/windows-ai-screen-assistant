using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ScreenAssistant.Core.AIClient;

namespace ScreenAssistant.Tests;

public sealed class AiClientTests
{
    private static readonly Uri Endpoint = new("https://example.test/custom/v1/");
    private static readonly ImagePayload Frame = new("image/png", [1, 2, 3]);

    [Fact]
    public async Task SendsMultimodalChatCompletionAndReadsText()
    {
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://example.test/custom/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer test-key", request.Headers.Authorization!.ToString());
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.Equal("vision-model", json.RootElement.GetProperty("model").GetString());
            Assert.False(json.RootElement.GetProperty("stream").GetBoolean());
            var content = json.RootElement.GetProperty("messages")[0].GetProperty("content");
            Assert.Equal("text", content[0].GetProperty("type").GetString());
            Assert.Equal("image_url", content[1].GetProperty("type").GetString());
            Assert.Equal("data:image/png;base64,AQID",
                content[1].GetProperty("image_url").GetProperty("url").GetString());
            return Json("""{"choices":[{"message":{"content":"Кадр получен"}}]}""");
        }));

        var result = await new AiClient(http).AnalyzeAsync(Endpoint, "test-key", "vision-model", Frame, default);
        Assert.Equal("Кадр получен", result);
    }

    [Theory]
    [InlineData("GREEN BLUE RED", true)]
    [InlineData("UNKNOWN", false)]
    public async Task RequiresCorrectAnswerToSyntheticImage(string answer, bool compatible)
    {
        var body = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = answer } } }
        });
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(Json(body))));
        var check = new AiClient(http).VerifyVisionAsync(
            Endpoint, "test-key", "vision-model", Frame, "GREEN BLUE RED", default);

        if (compatible)
            await check;
        else
            await Assert.ThrowsAsync<AiException>(() => check);
    }

    [Fact]
    public async Task FiltersCatalogByImageInputAndTextOutputWithoutSendingAKey()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Headers.Authorization);
            return Task.FromResult(Json("""
                {
                  "text-only": {"modalities":{"input":["text"],"output":["text"]}},
                  "image-output": {"modalities":{"input":["image"],"output":["image"]}},
                  "vision": {"modalities":{"input":["text","image"],"output":["text"]}},
                  "unknown": {}
                }
                """));
        }));
        Assert.Equal(new[] { "vision" }, await new AiClient(http).ListVisionModelsAsync(default));
    }

    [Fact]
    public async Task ExposesRetryAfterWithoutRepeatingThePost()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
            return Task.FromResult(response);
        }));
        var error = await Assert.ThrowsAsync<AiException>(
            () => new AiClient(http).AnalyzeAsync(Endpoint, "test-key", "vision", Frame, default));
        Assert.Equal(HttpStatusCode.TooManyRequests, error.Status);
        Assert.Equal(TimeSpan.FromSeconds(45), error.RetryAfter);
        Assert.False(error.RequiresConfigurationChange);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DefaultsRetryAfterToThirtySecondsWhenHeaderIsMissing()
    {
        using var http = new HttpClient(new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests))));
        var error = await Assert.ThrowsAsync<AiException>(
            () => new AiClient(http).AnalyzeAsync(Endpoint, "test-key", "vision", Frame, default));
        Assert.Equal(TimeSpan.FromSeconds(30), error.RetryAfter);
        Assert.False(error.RequiresConfigurationChange);
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect, true)]
    [InlineData(HttpStatusCode.MovedPermanently, true)]
    [InlineData(HttpStatusCode.BadRequest, true)]
    [InlineData(HttpStatusCode.Unauthorized, true)]
    [InlineData(HttpStatusCode.PaymentRequired, true)]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.NotFound, true)]
    [InlineData(HttpStatusCode.RequestTimeout, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public async Task ClassifiesConfigurationChangesByStatus(HttpStatusCode status, bool requiresChange)
    {
        using var http = new HttpClient(new Handler((_, _) =>
            Task.FromResult(new HttpResponseMessage(status))));
        var error = await Assert.ThrowsAsync<AiException>(
            () => new AiClient(http).AnalyzeAsync(Endpoint, "test-key", "vision", Frame, default));
        Assert.Equal(requiresChange, error.RequiresConfigurationChange);
        Assert.Equal(status, error.Status);
    }

    [Fact]
    public async Task NormalizesBaseUriWithoutTrailingSlashForCompletionsPath()
    {
        using var http = new HttpClient(new Handler((request, _) =>
        {
            Assert.Equal("https://example.test/custom/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(Json("""{"choices":[{"message":{"content":"ok"}}]}"""));
        }));
        var result = await new AiClient(http).AnalyzeAsync(
            new Uri("https://example.test/custom/v1"), "test-key", "vision", Frame, default);
        Assert.Equal("ok", result);
    }

    [Fact]
    public async Task MapsIoExceptionsFromNonStreamingRequests()
    {
        using var http = new HttpClient(new Handler((_, _) => throw new IOException("reset")));
        var error = await Assert.ThrowsAsync<AiException>(
            () => new AiClient(http).AnalyzeAsync(Endpoint, "test-key", "vision", Frame, default));
        Assert.Contains("Соединение с API прервалось", error.Message);
        Assert.DoesNotContain("reset", error.ToString());
    }

    [Fact]
    public async Task KeepsProviderErrorBodiesOutOfExceptions()
    {
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("private-key") })));
        var error = await Assert.ThrowsAsync<AiException>(
            () => new AiClient(http).AnalyzeAsync(Endpoint, "private-key", "vision", Frame, default));

        Assert.True(error.RequiresConfigurationChange);
        Assert.DoesNotContain("private-key", error.ToString());
    }

    [Fact]
    public async Task DistinguishesCallerCancellationFromTimeout()
    {
        using var http = new HttpClient(new Handler(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("{}");
        }));
        var client = new AiClient(http, TimeSpan.FromMilliseconds(80));
        var timeout = await Assert.ThrowsAsync<AiException>(
            () => client.AnalyzeAsync(Endpoint, "test-key", "vision", Frame, default));
        Assert.Contains("Время ожидания", timeout.Message);

        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.AnalyzeAsync(Endpoint, "test-key", "vision", Frame, stop.Token));
    }

    [Theory]
    [InlineData("https://example.test/custom/v1", true)]
    [InlineData("http://example.test/v1", false)]
    [InlineData("https://user:password@example.test/v1", false)]
    [InlineData("https://example.test/v1?token=secret", false)]
    [InlineData("https://example.test/v1#fragment", false)]
    public void ValidatesBaseUrl(string value, bool expected)
    {
        Assert.Equal(expected, AiClient.TryParseBaseUrl(value, out var uri));
        if (expected)
            Assert.Equal(Endpoint, uri);
    }

    [Fact]
    public async Task StreamsTextBeforeTheResponseCompletes()
    {
        var first = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            var stream = new GatedStream(
                ": processing\n\ndata: {\"choices\":[{\"delta\":{\"content\":\"7\"}}]}\n\n",
                "data: {\"choices\":[{\"delta\":{\"content\":\" шоколадок\"},\"finish_reason\":\"stop\"}]}\n\n"
                    + "data: {\"choices\":[],\"usage\":{}}\n\ndata: [DONE]\n\n",
                release.Task);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream) { Headers = { ContentType = new("text/event-stream") } }
            };
        }));
        var chunks = new List<string>();
        var task = new AiClient(http).AnalyzeStreamingAsync(Endpoint, "test-key", "vision", Frame, delta =>
        {
            chunks.Add(delta);
            first.TrySetResult(delta);
            return Task.CompletedTask;
        }, default);

        try
        {
            Assert.Equal("7", await first.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(task.IsCompleted);
        }
        finally { release.TrySetResult(); }

        Assert.Equal("7 шоколадок", await task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("7 шоколадок", string.Concat(chunks));
    }

    [Theory]
    [InlineData("data: {\"error\":{\"message\":\"private payload\"}}\n\n")]
    [InlineData("")]
    public async Task RejectsInterruptedOrErroredStreams(string ending)
    {
        var body = "data: {\"choices\":[{\"delta\":{\"content\":\"Неполный ответ\"}}]}\n\n" + ending;
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
            })));
        var error = await Assert.ThrowsAsync<AiException>(() => new AiClient(http).AnalyzeStreamingAsync(
            Endpoint, "test-key", "vision", Frame, _ => Task.CompletedTask, default));
        Assert.DoesNotContain("private payload", error.ToString());
    }

    [Fact]
    public async Task HandlesJsonCompletionFromCompatibleEndpointWithoutAnotherRequest()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(Json("""{"choices":[{"message":{"content":"Готово"}}]}"""));
        }));
        var result = await new AiClient(http).AnalyzeStreamingAsync(Endpoint, "test-key", "vision",
            Frame, _ => throw new InvalidOperationException("JSON has no partial updates."), default);
        Assert.Equal("Готово", result);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CancelsAnOpenStreamAfterDeliveringTheFirstChunk()
    {
        using var stop = new CancellationTokenSource();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new Handler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new GatedStream(
                    "data: {\"choices\":[{\"delta\":{\"content\":\"7\"}}]}\n\n", "", release.Task))
                {
                    Headers = { ContentType = new("text/event-stream") }
                }
            })));
        var task = new AiClient(http).AnalyzeStreamingAsync(Endpoint, "test-key", "vision", Frame, _ =>
        {
            first.TrySetResult();
            return Task.CompletedTask;
        }, stop.Token);
        await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private sealed class GatedStream(string first, string second, Task release) : Stream
    {
        private readonly MemoryStream _first = new(Encoding.UTF8.GetBytes(first));
        private readonly MemoryStream _second = new(Encoding.UTF8.GetBytes(second));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await _first.ReadAsync(buffer, cancellationToken);
            if (count > 0)
                return count;

            await release.WaitAsync(cancellationToken);
            return await _second.ReadAsync(buffer, cancellationToken);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _first.Dispose();
                _second.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task ScrollAnalysisSendsFramesInOrderInOneRequest()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler(async (request, token) =>
        {
            calls++;
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
            var messages = json.RootElement.GetProperty("messages");
            Assert.Equal(1, messages.GetArrayLength());
            var parts = messages[0].GetProperty("content");
            Assert.Equal(3, parts.GetArrayLength());
            Assert.Equal("data:image/png;base64,AQID", parts[1].GetProperty("image_url").GetProperty("url").GetString());
            Assert.Equal("data:image/jpeg;base64,BAU=", parts[2].GetProperty("image_url").GetProperty("url").GetString());
            return Json("""{"choices":[{"message":{"content":"Готово"}}]}""");
        }));

        var result = await new AiClient(http).AnalyzeScrollAsync(Endpoint, "test", "vision",
            [Frame, new("image/jpeg", [4, 5])], default);
        Assert.Equal("Готово", result);
        Assert.Equal(1, calls);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
