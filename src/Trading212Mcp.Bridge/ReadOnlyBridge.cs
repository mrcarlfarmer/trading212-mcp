using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Trading212Mcp.Bridge;

public sealed class ReadOnlyBridge(McpClient remote)
{
    private static readonly HashSet<string> AllowedNames = new(StringComparer.Ordinal)
    {
        "get_account_summary", "get_positions", "get_pending_orders", "get_pending_order",
        "search_instruments", "get_instrument", "get_exchanges",
        "get_order_history", "get_dividend_history", "get_transactions"
    };

    public async Task<ListToolsResult> ListAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tools = await remote.ListToolsAsync(cancellationToken: cancellationToken);
            return new ListToolsResult
            {
                Tools = tools.Where(tool => AllowedNames.Contains(tool.Name) &&
                    tool.ProtocolTool.Annotations?.ReadOnlyHint == true).Select(tool => tool.ProtocolTool).ToList()
            };
        }
        catch (Exception exception) when (exception is HttpRequestException or McpException)
        {
            Console.Error.WriteLine("Pi MCP tool discovery failed.");
            throw new McpException("Pi MCP tool discovery failed. Check network access and server status.");
        }
    }

    public async Task<CallToolResult> CallAsync(CallToolRequestParams? parameters, CancellationToken cancellationToken)
    {
        if (parameters is null || !AllowedNames.Contains(parameters.Name))
        {
            return Error("Unknown read-only tool.");
        }
        try
        {
            return await remote.CallToolAsync(parameters, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or McpException)
        {
            Console.Error.WriteLine("Pi MCP tool forwarding failed.");
            return Error("Pi MCP connection failed. Check network access and server status.");
        }
    }

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };
}
