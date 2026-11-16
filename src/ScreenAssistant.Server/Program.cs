using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using ScreenAssistant.Core.AIClient;
using ScreenAssistant.Server;

if (args is ["--backup", var destination])
{
    var source = Environment.GetEnvironmentVariable("Licensing__DatabasePath");
    if (string.IsNullOrWhiteSpace(source))
        throw new InvalidOperationException("Set Licensing__DatabasePath to the existing license database.");

    LicenseDatabaseBackup.Create(source, destination);
    Console.WriteLine("License database backup created and validated.");
    return;
}

var server = ServerApplication.Build(WebApplication.CreateBuilder(args));
await server.RunAsync();

internal static class ServerApplication
{
    internal static WebApplication Build(WebApplicationBuilder builder, Action<IServiceCollection>? configureProvider = null)
    {
        var databasePath = builder.Configuration["Licensing:DatabasePath"];
        var operatorToken = builder.Configuration["Licensing:OperatorToken"];
        if (string.IsNullOrWhiteSpace(databasePath) || operatorToken is not { Length: 64 }
            || !operatorToken.All(Uri.IsHexDigit))
            throw new InvalidOperationException("Set Licensing:DatabasePath and a random 64-digit hexadecimal Licensing:OperatorToken.");

        var operatorHash = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + operatorToken));
        var allowLoopbackHttp = builder.Configuration.GetValue<bool>("Hosting:AllowLoopbackHttp");
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 4096);
        var providerKey = builder.Configuration["Analysis:ProviderKey"];
        var model = builder.Configuration["Analysis:Model"];
        if (string.IsNullOrWhiteSpace(providerKey) != string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Configure both Analysis:ProviderKey and Analysis:Model.");

        var dailyLimit = builder.Configuration.GetValue<long>("Analysis:DailyCreditLimit");
        if (!string.IsNullOrWhiteSpace(providerKey) && dailyLimit is < 1 or > LicenseStore.MaximumCredits)
            throw new InvalidOperationException("Set Analysis:DailyCreditLimit to a positive pilot credit budget.");

        builder.Services.AddHttpClient<AiClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddConcurrencyLimiter("analysis", limiter =>
            {
                limiter.PermitLimit = 4;
                limiter.QueueLimit = 0;
            });
            options.AddFixedWindowLimiter("activation", limiter =>
            {
                limiter.PermitLimit = 60;
                limiter.Window = TimeSpan.FromMinutes(1);
                limiter.QueueLimit = 0;
            });
        });

        var store = new LicenseStore(Path.GetFullPath(databasePath), TimeProvider.System);
        store.Initialize();

        configureProvider?.Invoke(builder.Services);
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps
                && (!allowLoopbackHttp || context.Connection.RemoteIpAddress is not { } peer
                    || !System.Net.IPAddress.IsLoopback(peer)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
        });
        app.UseRateLimiter();

        app.MapGet("/health", () => Results.NoContent());
        var admin = app.MapGroup("/v1/operator")
            .AddEndpointFilter(async (context, next) =>
            {
                var header = context.HttpContext.Request.Headers.Authorization;
                if (header.Count != 1 || header[0] is not { Length: 71 } supplied
                    || !CryptographicOperations.FixedTimeEquals(operatorHash,
                        SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
                    return Results.Unauthorized();

                return await next(context);
            });
        admin.MapPost("/licenses", () => Results.Ok(store.Issue()));
        admin.MapDelete("/licenses/{id:guid}", (Guid id) =>
            store.Revoke(id.ToString("N")) ? Results.NoContent() : Results.NotFound());

        admin.MapPut("/licenses/{licenseId:guid}/credit-grants/{grantId:guid}",
            (Guid licenseId, Guid grantId, CreditGrant request) => store.GrantCredits(licenseId, grantId, request.Credits) switch
            {
                CreditGrantResult.Applied => Results.NoContent(),
                CreditGrantResult.Invalid => Results.BadRequest(),
                CreditGrantResult.NotFound => Results.NotFound(),
                _ => Results.Conflict()
            });

        admin.MapGet("/audit", (long? after) => after is < 0
            ? Results.BadRequest()
            : Results.Ok(store.ReadOperatorAudit(after ?? 0)));
        admin.MapGet("/analysis/pending", () => Results.Ok(store.ListPendingAnalysis()));
        admin.MapPut("/licenses/{licenseId:guid}/analysis/{operationId:guid}/settlement",
            (Guid licenseId, Guid operationId, AnalysisSettlement request) =>
            {
                if (request.ActualCredits is not (>= 0 and <= LicenseStore.MaximumCredits))
                    return Results.BadRequest();

                return store.SettleAnalysis(licenseId, operationId, request.ActualCredits.Value, byOperator: true)
                    ? Results.NoContent()
                    : Results.Conflict();
            });

        var activation = app.MapGroup("/v1/activation").RequireRateLimiting("activation");
        activation.MapPost("/challenge", (StartActivation request) =>
            store.Start(request.ActivationCode, request.PublicKey) is { } challenge
                ? Results.Ok(challenge)
                : Results.BadRequest());
        activation.MapPost("/complete", (CompleteActivation request) =>
            store.Complete(request.ChallengeId, request.Signature) is { } licenseId
                ? Results.Ok(new { licenseId })
                : Results.BadRequest());

        app.MapPost("/v1/licenses/status", (LicenseStatus request, HttpResponse response) =>
        {
            if (store.ReadAvailableCredits(request.LicenseId, request.Timestamp, request.Signature) is not { } credits)
                return Results.Unauthorized();

            response.Headers["X-Available-Credits"] = credits.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Results.NoContent();
        }).RequireRateLimiting("activation");

        if (!string.IsNullOrWhiteSpace(providerKey) && !string.IsNullOrWhiteSpace(model))
        {
            var provider = new Uri(AiClient.DefaultBaseUrl);
            app.MapPost("/v1/analysis", async (HttpContext context, AiClient client,
                ILogger<AnalysisRequest> logger) =>
            {
                var bodySize = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (bodySize is { IsReadOnly: false })
                    bodySize.MaxRequestBodySize = AnalysisEndpoint.MaxBodyBytes;

                if (!context.Request.HasJsonContentType())
                    return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

                AnalysisRequest? request;
                try
                {
                    request = await context.Request.ReadFromJsonAsync<AnalysisRequest>(context.RequestAborted);
                }
                catch (System.Text.Json.JsonException)
                {
                    return Results.BadRequest();
                }
                return await AnalysisEndpoint.RunAsync(request, store,
                    (frames, series) => series
                        ? client.AnalyzeScrollAsync(provider, providerKey, model, frames, CancellationToken.None)
                        : client.AnalyzeAsync(provider, providerKey, model, frames[0], CancellationToken.None), logger, dailyLimit);
            }).RequireRateLimiting("analysis");
        }

        return app;
    }
}

internal sealed record StartActivation(string? ActivationCode, byte[]? PublicKey);
internal sealed record CompleteActivation(string? ChallengeId, byte[]? Signature);

internal sealed record LicenseStatus(Guid LicenseId, long Timestamp, byte[]? Signature);

internal sealed record CreditGrant(long Credits);

internal sealed record AnalysisSettlement(long? ActualCredits);
