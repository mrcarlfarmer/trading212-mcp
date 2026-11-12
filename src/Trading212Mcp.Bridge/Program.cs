using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Trading212Mcp.Bridge;

var url = Environment.GetEnvironmentVariable("MCP_SERVER_URL");
var tokenFile = Environment.GetEnvironmentVariable("MCP_ACCESS_TOKEN_FILE");
if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "https" ||
    !string.IsNullOrEmpty(endpoint.UserInfo) || endpoint.AbsolutePath != "/mcp" ||
    !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment) ||
    string.IsNullOrWhiteSpace(tokenFile) || !Path.IsPathFullyQualified(tokenFile))
{
    Console.Error.WriteLine("Set MCP_SERVER_URL to the HTTPS /mcp endpoint and MCP_ACCESS_TOKEN_FILE to an absolute local MCP token file.");
    return 1;
}
string token;
try
{
    token = File.ReadAllText(tokenFile).TrimEnd('\r', '\n');
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("MCP token file could not be read.");
    return 1;
}
if (token.Length is < 32 or > 512 || token.Any(char.IsWhiteSpace))
{
    Console.Error.WriteLine("MCP token must contain 32-512 non-whitespace characters.");
    return 1;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
using var httpClient = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false });
httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
await using var transport = new HttpClientTransport(new HttpClientTransportOptions
{
    Endpoint = endpoint,
    TransportMode = HttpTransportMode.StreamableHttp,
    ConnectionTimeout = TimeSpan.FromSeconds(15)
}, httpClient);

McpClient remote;
try
{
    remote = await McpClient.CreateAsync(transport, cancellationToken: cancellation.Token);
}
catch (Exception exception) when (exception is HttpRequestException or McpException or OperationCanceledException)
{
    Console.Error.WriteLine("Could not connect to the Pi MCP server. Check HTTPS trust, network access, and the MCP token.");
    return 1;
}
await using (remote)
{
    var bridge = new ReadOnlyBridge(remote);
    var builder = Host.CreateApplicationBuilder(args);
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.Logging.AddFilter("ModelContextProtocol", LogLevel.None);
    builder.Services.AddMcpServer()
        .WithStdioServerTransport()
        .WithListToolsHandler(async (_, ct) => await bridge.ListAsync(ct))
        .WithCallToolHandler(async (context, ct) => await bridge.CallAsync(context.Params, ct));
    await builder.Build().RunAsync(cancellation.Token);
}
return 0;
