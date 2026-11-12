using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Trading212Mcp.Tests;

public sealed class TestHost : WebApplicationFactory<Program>
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "trading212-mcp-test-" + Guid.NewGuid());
    public const string ApiKey = "synthetic-broker-key-canary";
    public const string ApiSecret = "synthetic-broker-secret-canary";
    public const string Token = "synthetic-mcp-token-with-at-least-32-characters";
    public StubHandler Handler { get; } = new();
    public TestClock Clock { get; } = new();
    public Dictionary<string, string?> Settings { get; }

    public TestHost()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "key"), ApiKey);
        File.WriteAllText(Path.Combine(directory, "secret"), ApiSecret);
        File.WriteAllText(Path.Combine(directory, "token"), Token);
        if (OperatingSystem.IsLinux())
        {
            foreach (var name in new[] { "key", "secret", "token" })
            {
                File.SetUnixFileMode(Path.Combine(directory, name), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        Settings = new()
        {
            ["Trading212:Environment"] = "Live",
            ["Trading212:ApiKeyFile"] = Path.Combine(directory, "key"),
            ["Trading212:ApiSecretFile"] = Path.Combine(directory, "secret"),
            ["Mcp:AccessTokenFile"] = Path.Combine(directory, "token"),
            ["AllowedHosts"] = "localhost;127.0.0.1",
            ["Mcp:AllowedOrigins"] = "https://localhost"
        };
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(Settings));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new StubFactory(Handler));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }
}

public sealed class StubHandler : HttpMessageHandler
{
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Response { get; set; } =
        (_, _) => Task.FromResult(Json("""{"id":9223372036854775807,"currency":"GBP","cash":{"availableToTrade":100.25},"investments":{"currentValue":200.50},"totalValue":300.75}"""));
    public List<(HttpMethod Method, Uri? Uri, string? Authorization)> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (Requests)
        {
            Requests.Add((request.Method, request.RequestUri, request.Headers.Authorization?.ToString()));
        }
        return Response(request, cancellationToken);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
}

public sealed class StubFactory(StubHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

public sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan duration) => now += duration;
}
