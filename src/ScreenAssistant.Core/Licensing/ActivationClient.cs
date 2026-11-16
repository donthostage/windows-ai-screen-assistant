using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using ScreenAssistant.Core.AIClient;

namespace ScreenAssistant.Core.Licensing;

public sealed class ActivationClient(HttpClient http)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool TryParseServer(string value, out Uri? server)
    {
        if (Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && uri.AbsolutePath == "/")
        {
            server = uri;
            return true;
        }

        server = null;
        return false;
    }

    public async Task<Guid> ActivateAsync(Uri server, string code, ECDsa device, CancellationToken token)
    {
        if (!TryParseServer(server.AbsoluteUri, out _))
            throw new ArgumentException("Invalid licensing server address.", nameof(server));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var challenge = await PostAsync<Challenge>(new Uri(server, "v1/activation/challenge"),
            new { activationCode = code, publicKey = device.ExportSubjectPublicKeyInfo() }, timeout.Token);
        if (!Guid.TryParseExact(challenge.ChallengeId, "N", out _) || challenge.Nonce is not { Length: 32 })
            throw new InvalidDataException("Invalid activation challenge.");

        var signature = DeviceActivationProof.Sign(device, challenge.Nonce);
        var result = await PostAsync<Receipt>(new Uri(server, "v1/activation/complete"),
            new { challenge.ChallengeId, signature }, timeout.Token);
        if (!Guid.TryParseExact(result.LicenseId, "N", out var licenseId))
            throw new InvalidDataException("Invalid activation receipt.");

        return licenseId;
    }

    public async Task<bool> VerifyStatusAsync(Uri server, Guid licenseId, ECDsa device,
        CancellationToken token) => await ReadAvailableCreditsAsync(server, licenseId, device, token) is not null;

    public async Task<long?> ReadAvailableCreditsAsync(Uri server, Guid licenseId, ECDsa device,
        CancellationToken token)
    {
        if (!TryParseServer(server.AbsoluteUri, out _))
            throw new ArgumentException("Invalid licensing server address.", nameof(server));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = DeviceActivationProof.SignLicenseStatus(device, licenseId, timestamp);
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(server, "v1/licenses/status"))
        {
            Content = JsonContent.Create(new { licenseId, timestamp, signature })
        };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;

        response.EnsureSuccessStatusCode();
        if (response.StatusCode != HttpStatusCode.NoContent
            || !response.Headers.TryGetValues("X-Available-Credits", out var values)
            || !long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var credits))
            throw new InvalidDataException("Unexpected licensing status response.");

        return credits;
    }

    public Task<string> AnalyzeAsync(Uri server, Guid licenseId, Guid operationId,
        ECDsa device, ImagePayload image, CancellationToken token)
    {
        if (!AnalysisFrames.IsValidFrame(image))
            throw new AiException("Кадр превышает лимит сервера или имеет неподдерживаемый формат.");

        return SubmitAnalysisAsync(server, licenseId, operationId, device,
            DeviceActivationProof.HashAnalysisFrame(image.MediaType, image.Bytes),
            (timestamp, signature) => new { licenseId, operationId, timestamp, signature, image.MediaType, frame = image.Bytes },
            token);
    }

    public Task<string> AnalyzeSeriesAsync(Uri server, Guid licenseId, Guid operationId,
        ECDsa device, IReadOnlyList<ImagePayload> frames, CancellationToken token)
    {
        if (!AnalysisFrames.IsValidSeries(frames))
            throw new AiException("Серия превышает лимит сервера или содержит неподдерживаемый кадр.");

        return SubmitAnalysisAsync(server, licenseId, operationId, device,
            AnalysisFrames.HashSeries(frames),
            (timestamp, signature) => new { licenseId, operationId, timestamp, signature, frames }, token);
    }

    private async Task<string> SubmitAnalysisAsync(Uri server, Guid licenseId, Guid operationId,
        ECDsa device, byte[] hash, Func<long, byte[], object> payload, CancellationToken token)
    {
        if (!TryParseServer(server.AbsoluteUri, out _))
            throw new ArgumentException("Invalid licensing server address.", nameof(server));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(75));
        try
        {
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var signature = DeviceActivationProof.SignAnalysis(device, licenseId, operationId, timestamp, hash);
            var reply = await PostAsync<AnalysisReply>(new Uri(server, "v1/analysis"),
                payload(timestamp, signature), timeout.Token, 512 * 1024);
            return string.IsNullOrWhiteSpace(reply.Text)
                ? throw new AiException("Сервер не вернул текст ответа.")
                : reply.Text;
        }
        catch (HttpRequestException ex)
        {
            var message = ex.StatusCode switch
            {
                HttpStatusCode.PaymentRequired => "Недостаточно кредитов. Пополните лимит у оператора.",
                HttpStatusCode.Unauthorized => "Сервер не подтвердил лицензию или ключ устройства.",
                HttpStatusCode.Conflict => "Этот запрос уже принят сервером.",
                HttpStatusCode.NotFound => "Анализ на сервере лицензий не включён.",
                HttpStatusCode.TooManyRequests => "Сервер достиг лимита анализа. Повторите позже.",
                HttpStatusCode.BadGateway => "Сервер не подтвердил ответ модели. Резерв требует сверки.",
                _ => "Не удалось выполнить анализ через сервер лицензий."
            };
            throw new AiException(message, ex.StatusCode,
                ex.StatusCode == HttpStatusCode.TooManyRequests ? TimeSpan.FromMinutes(1) : null);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new AiException("Время ожидания сервера лицензий истекло.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException)
        {
            throw new AiException("Сервер вернул некорректный ответ.");
        }
    }

    private async Task<T> PostAsync<T>(Uri endpoint, object payload, CancellationToken token, int limit = 4096)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = JsonContent.Create(payload) };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(token);
        var buffer = new byte[limit + 1];
        var bytesRead = await body.ReadAtLeastAsync(buffer, buffer.Length, false, token);
        if (bytesRead == buffer.Length)
            throw new InvalidDataException("Server response exceeded the size limit.");

        return JsonSerializer.Deserialize<T>(buffer.AsSpan(0, bytesRead), JsonOptions)
            ?? throw new InvalidDataException("Empty server response.");
    }

    private sealed record Challenge(string? ChallengeId, byte[]? Nonce);
    private sealed record AnalysisReply(string? Text);
    private sealed record Receipt(string? LicenseId);
}
