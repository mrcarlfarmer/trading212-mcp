using System.Security.Cryptography;
using System.Text;
using Trading212Mcp.Server.Configuration;
using Trading212Mcp.Server.Tools;
using Trading212Mcp.Server.Trading212;

if (args.Contains("--health-check", StringComparer.Ordinal))
{
    using var healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
    try
    {
        using var result = await healthClient.GetAsync("http://127.0.0.1:8080/health/ready");
        return result.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("ModelContextProtocol", LogLevel.Warning);
builder.Services.AddSingleton(provider => ServerSettings.Load(provider.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient("broker", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
}).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
  .RemoveAllLoggers();
builder.Services.AddSingleton<Trading212Client>();
builder.Services.AddSingleton<ReadTools>();
builder.Services.AddMcpServer(options => options.ServerInstructions =
    "Read-only Trading 212 account access. No trading or report generation. Results explicitly identify Live or Demo. History tools return one bounded page; use continuationToken alone for subsequent pages. Respect rate_limited errors and retryAt; metadata is cached for 10 minutes.")
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<ReadTools>();

var app = builder.Build();
ServerSettings settings;
try
{
    settings = app.Services.GetRequiredService<ServerSettings>();
}
catch (InvalidOperationException exception)
{
    Console.Error.WriteLine(exception.Message);
    await app.DisposeAsync();
    return 1;
}
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp"))
    {
        if (context.Request.Headers.TryGetValue("Origin", out var origins) &&
            (origins.Count != 1 || !settings.AllowedOrigins.Contains(origins[0]!)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            authorization.Length > prefix.Length + 512 ||
            !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(authorization[prefix.Length..])), settings.TokenHash))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers.WWWAuthenticate = "Bearer";
            return;
        }
        context.Response.Headers.CacheControl = "no-store";
    }
    await next(context);
});
app.MapGet("/health/live", () => Results.Ok(new { status = "healthy" }));
app.MapGet("/health/ready", () => Results.Ok(new { status = "ready" }));
app.MapMcp("/mcp");
await app.RunAsync();
return 0;

public partial class Program;
