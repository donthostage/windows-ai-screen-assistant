using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ScreenAssistant.Core.AIClient;
using ScreenAssistant.Core.Licensing;
using ScreenAssistant.Server;

namespace ScreenAssistant.Tests;

public sealed class AnalysisHttpTests
{
    [Fact]
    public async Task SignedSeriesUsesConfiguredProviderAndBudgetOverHttp()
    {
        var folder = Directory.CreateTempSubdirectory("screen-assistant-analysis-http-");
        var database = Path.Combine(folder.FullName, "test.db");
        try
        {
            using var tlsKey = RSA.Create(2048);
            var certificateRequest = new CertificateRequest("CN=ScreenAssistant HTTPS test", tlsKey,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            certificateRequest.CertificateExtensions.Add(names.Build());
            using var generated = certificateRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx));
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0);
                options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate));
            });
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Licensing:DatabasePath"] = database,
                ["Licensing:OperatorToken"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                ["Analysis:ProviderKey"] = "test-provider-key",
                ["Analysis:Model"] = "test-model",
                ["Analysis:DailyCreditLimit"] = "2"
            });
            var provider = new ProviderStub();
            await using var server = ServerApplication.Build(builder, services =>
                services.AddHttpClient<AiClient>().ConfigurePrimaryHttpMessageHandler(() => provider));
            await server.StartAsync();

            var store = new LicenseStore(database, TimeProvider.System);
            var issued = store.Issue();
            var id = Guid.ParseExact(issued.LicenseId, "N");
            Assert.Equal(CreditGrantResult.Applied, store.GrantCredits(id, Guid.NewGuid(), 5));

            using var transport = new HttpClient(new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, presented, _, _) =>
                    presented?.RawData.AsSpan().SequenceEqual(certificate.RawData) == true
            });
            var client = new ActivationClient(transport);
            using var device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var url = new Uri(Assert.Single(server.Urls, address => address.StartsWith("https://", StringComparison.Ordinal)));
            var plain = Assert.Single(server.Urls, address => address.StartsWith("http://", StringComparison.Ordinal));
            using var rejected = await transport.GetAsync(plain + "/health");
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
            Assert.Equal(id, await client.ActivateAsync(url, issued.ActivationCode, device, default));
            ImagePayload[] frames = [new("image/png", [1, 2]), new("image/jpeg", [3, 4])];
            var operation = Guid.NewGuid();
            Assert.Equal("Ответ сервера", await client.AnalyzeSeriesAsync(url, id, operation, device, frames, default));
            Assert.Equal(3L, await client.ReadAvailableCreditsAsync(url, id, device, default));

            var duplicate = await Assert.ThrowsAsync<AiException>(
                () => client.AnalyzeSeriesAsync(url, id, operation, device, frames, default));
            Assert.Equal(HttpStatusCode.Conflict, duplicate.Status);
            var limit = await Assert.ThrowsAsync<AiException>(
                () => client.AnalyzeAsync(url, id, Guid.NewGuid(), device, frames[0], default));
            Assert.Equal(HttpStatusCode.TooManyRequests, limit.Status);
            Assert.Equal(1, provider.Calls);
            Assert.Equal(3L, await client.ReadAvailableCreditsAsync(url, id, device, default));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var file in folder.EnumerateFiles())
                file.Delete();
            folder.Delete();
        }
    }

    private sealed class ProviderStub : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            Assert.Equal("api.aitunnel.ru", request.RequestUri?.Host);
            Assert.Equal("test-provider-key", request.Headers.Authorization?.Parameter);
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>(token);
            Assert.Equal("test-model", body.GetProperty("model").GetString());
            var parts = body.GetProperty("messages")[0].GetProperty("content");
            Assert.Equal("data:image/png;base64,AQI=", parts[1].GetProperty("image_url").GetProperty("url").GetString());
            Assert.Equal("data:image/jpeg;base64,AwQ=", parts[2].GetProperty("image_url").GetProperty("url").GetString());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"choices":[{"message":{"content":"Ответ сервера"}}]}""")
            };
        }
    }
}
