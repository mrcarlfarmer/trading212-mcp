using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Trading212Mcp.Server.Configuration;
using Trading212Mcp.Server.Trading212;

namespace Trading212Mcp.Server.Tools;

[McpServerToolType]
public sealed class ReadTools(
    Trading212Client broker,
    ServerSettings settings,
    ILogger<ReadTools> logger)
{
    [McpServerTool(Name = "get_account_summary", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Read account cash and investment summary in primary account currency. Never places trades. Results identify Live or Demo and cache age.")]
    public Task<CallToolResult> AccountSummary(CancellationToken cancellationToken) =>
        Execute(() => Read("account/summary", cancellationToken));

    [McpServerTool(Name = "get_positions", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Read owned positions, optionally by exact Trading 212 ticker. Prices use instrument currency; walletImpact uses account currency. Local paging reports omitted positions.")]
    public Task<CallToolResult> Positions(
        CancellationToken cancellationToken,
        string? ticker = null, int limit = 20, int offset = 0) => Execute(async () =>
    {
        ValidateTicker(ticker);
        var path = "positions" + (ticker is null ? "" : "?ticker=" + ticker);
        return await ReadArray(path, limit, offset, cancellationToken);
    });

    [McpServerTool(Name = "get_pending_orders", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Read active orders with local paging. Filled/cancelled orders are not included; use history for completed orders.")]
    public Task<CallToolResult> PendingOrders(
        CancellationToken cancellationToken, int limit = 20, int offset = 0) =>
        Execute(() => ReadArray("orders", limit, offset, cancellationToken));

    [McpServerTool(Name = "get_pending_order", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Read one pending order by decimal-string ID. Not found does not prove cancellation or non-execution.")]
    public Task<CallToolResult> PendingOrder(string orderId, CancellationToken cancellationToken) => Execute(() =>
    {
        if (!long.TryParse(orderId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
        {
            throw Input("orderId must be a positive 64-bit decimal ID.");
        }
        return Read("orders/" + id.ToString(CultureInfo.InvariantCulture), cancellationToken);
    });

    [McpServerTool(Name = "search_instruments", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Search cached broker instrument metadata by name, short name, exact identifier, or ISIN. Returns bounded local pages, not market quotes. Metadata is cached for 10 minutes.")]
    public Task<CallToolResult> SearchInstruments(
        string query, CancellationToken cancellationToken, int limit = 20, int offset = 0) => Execute(async () =>
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length is < 2 or > 100)
        {
            throw Input("query must contain 2-100 characters.");
        }
        ValidatePaging(limit, offset);
        var result = await broker.GetAsync(ReadPolicy.Prefix + "metadata/instruments", cancellationToken);
        var matches = ((JsonArray)result.Data).Where(node => node is JsonObject item &&
            new[] { "name", "shortName", "ticker", "isin" }.Any(field =>
                item[field] is JsonValue value && value.TryGetValue<string>(out var text) &&
                text.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        return Envelope(result, Page(matches, limit, offset));
    });

    [McpServerTool(Name = "get_instrument", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Read cached metadata for an exact Trading 212 ticker such as AAPL_US_EQ. Does not provide a general quote feed.")]
    public Task<CallToolResult> Instrument(string ticker, CancellationToken cancellationToken) => Execute(async () =>
    {
        ValidateTicker(ticker);
        var result = await broker.GetAsync(ReadPolicy.Prefix + "metadata/instruments", cancellationToken);
        var item = ((JsonArray)result.Data).FirstOrDefault(node =>
            node?["ticker"] is JsonValue value && value.TryGetValue<string>(out var text) &&
            string.Equals(text, ticker, StringComparison.Ordinal));
        if (item is null)
        {
            throw new BrokerException("not_found", "Exact Trading 212 instrument ticker was not found.");
        }
        return Envelope(result, item.DeepClone());
    });

    [McpServerTool(Name = "get_exchanges", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Read cached exchange metadata and working schedules with bounded local paging.")]
    public Task<CallToolResult> Exchanges(
        CancellationToken cancellationToken, int limit = 20, int offset = 0) =>
        Execute(() => ReadArray("metadata/exchanges", limit, offset, cancellationToken));

    [McpServerTool(Name = "get_order_history", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Read one historical orders page, maximum 50 records. Optional ticker on first page. Supply only continuationToken for subsequent pages. No implicit full-history scan.")]
    public Task<CallToolResult> OrderHistory(
        CancellationToken cancellationToken, int limit = 20, string? ticker = null, string? continuationToken = null) =>
        Execute(() => History("orders", limit, ticker, null, continuationToken, cancellationToken));

    [McpServerTool(Name = "get_dividend_history", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Read one paid-dividends page, maximum 50 records. Optional ticker on first page. Supply only continuationToken for subsequent pages.")]
    public Task<CallToolResult> DividendHistory(
        CancellationToken cancellationToken, int limit = 20, string? ticker = null, string? continuationToken = null) =>
        Execute(() => History("dividends", limit, ticker, null, continuationToken, cancellationToken));

    [McpServerTool(Name = "get_transactions", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = true)]
    [Description("Read one account movements page, maximum 50 records. Optional starting time as an ISO-8601 UTC timestamp ending in Z. Supply only continuationToken for subsequent pages.")]
    public Task<CallToolResult> Transactions(
        CancellationToken cancellationToken, int limit = 20, string? time = null, string? continuationToken = null) =>
        Execute(() => History("transactions", limit, null, time, continuationToken, cancellationToken));

    private async Task<JsonObject> Read(string endpoint, CancellationToken cancellationToken)
    {
        var result = await broker.GetAsync(ReadPolicy.Prefix + endpoint, cancellationToken);
        return Envelope(result, result.Data);
    }

    private async Task<JsonObject> ReadArray(string endpoint, int limit, int offset, CancellationToken cancellationToken)
    {
        ValidatePaging(limit, offset);
        var result = await broker.GetAsync(ReadPolicy.Prefix + endpoint, cancellationToken);
        return Envelope(result, Page(((JsonArray)result.Data).ToArray(), limit, offset));
    }

    private async Task<JsonObject> History(
        string kind, int limit, string? ticker, string? time, string? continuation, CancellationToken cancellationToken)
    {
        ValidatePaging(limit, 0);
        ValidateTicker(ticker);
        var path = ReadPolicy.Prefix + "history/" + kind;
        if (continuation is not null)
        {
            if (limit != 20 || ticker is not null || time is not null)
            {
                throw Input("Supply only continuationToken when requesting the next page.");
            }
            path = DecodeContinuation(continuation, path);
        }
        else
        {
            path += "?limit=" + limit.ToString(CultureInfo.InvariantCulture);
            if (ticker is not null)
            {
                path += "&ticker=" + ticker;
            }
            if (time is not null)
            {
                if (!time.EndsWith('Z') || !DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out var timestamp))
                {
                    throw Input("time must be an ISO-8601 UTC timestamp ending in Z.");
                }
                path += "&time=" + Uri.EscapeDataString(timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            }
        }
        var result = await broker.GetAsync(path, cancellationToken);
        var page = (JsonObject)result.Data;
        var pageLimit = QueryHelpers.ParseQuery(new Uri(settings.BrokerOrigin, path).Query)
            .TryGetValue("limit", out var requestedLimit)
                ? int.Parse(requestedLimit.ToString(), CultureInfo.InvariantCulture) : 20;
        if (((JsonArray)page["items"]!).Count > pageLimit)
        {
            throw new BrokerException("invalid_response", "Broker history page exceeded the requested record limit.");
        }
        string? token = null;
        if (page["nextPagePath"] is JsonValue next)
        {
            var nextPath = next.GetValue<string>();
            try
            {
                ValidateContinuationPath(nextPath, ReadPolicy.Prefix + "history/" + kind);
            }
            catch (BrokerException)
            {
                throw new BrokerException("invalid_response", "Broker returned an unsafe or unsupported continuation path.");
            }
            token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(nextPath));
        }
        return Envelope(result, new JsonObject
        {
            ["items"] = page["items"]!.DeepClone(),
            ["continuationToken"] = token,
            ["hasMore"] = token is not null
        });
    }

    private static string DecodeContinuation(string token, string expectedPath)
    {
        if (token.Length is < 1 or > 4096)
        {
            throw Input("Invalid continuationToken.");
        }
        string path;
        try
        {
            path = new UTF8Encoding(false, true).GetString(WebEncoders.Base64UrlDecode(token));
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            throw Input("Invalid continuationToken.");
        }
        ValidateContinuationPath(path, expectedPath);
        return path;
    }

    private static void ValidateContinuationPath(string path, string expectedPath)
    {
        if (path.Length > 2048 || !string.Equals(path.Split('?', 2)[0], expectedPath, StringComparison.Ordinal))
        {
            throw Input("Continuation must address the same history endpoint.");
        }
        ReadPolicy.Validate(HttpMethod.Get, path);
    }

    private JsonObject Envelope(BrokerData result, JsonNode data)
    {
        var copy = data.DeepClone();
        NormalizeIds(copy);
        return new JsonObject
        {
            ["environment"] = settings.Environment,
            ["retrievedAt"] = result.RetrievedAt.ToString("O", CultureInfo.InvariantCulture),
            ["cached"] = result.Cached,
            ["data"] = copy
        };
    }

    private static JsonObject Page(JsonNode?[] items, int limit, int offset) => new()
    {
        ["items"] = new JsonArray(items.Skip(offset).Take(limit).Select(item => item!.DeepClone()).ToArray()),
        ["total"] = items.Length,
        ["offset"] = offset,
        ["nextOffset"] = (long)offset + limit < items.Length ? JsonValue.Create(offset + limit) : null
    };

    private static void NormalizeIds(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var key in obj.Select(pair => pair.Key).ToArray())
            {
                if ((key == "id" || key == "reportId" || key == "workingScheduleId") &&
                    obj[key] is JsonValue value && value.TryGetValue<long>(out var id))
                {
                    obj[key] = id.ToString(CultureInfo.InvariantCulture);
                }
                else if (obj[key] is { } child)
                {
                    NormalizeIds(child);
                }
            }
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item is not null)
                {
                    NormalizeIds(item);
                }
            }
        }
    }

    private async Task<CallToolResult> Execute(Func<Task<JsonObject>> action)
    {
        try
        {
            var payload = await action();
            var text = payload.ToJsonString();
            if (text.Length > 100_000)
            {
                throw new BrokerException("result_too_large", "Result exceeds output budget. Request a smaller page or narrower filter.");
            }
            return new CallToolResult
            {
                StructuredContent = JsonSerializer.SerializeToElement(payload),
                Content = [new TextContentBlock { Text = text }]
            };
        }
        catch (BrokerException exception)
        {
            logger.LogWarning("Read tool failed: {Code}.", exception.Code);
            var payload = new JsonObject
            {
                ["environment"] = settings.Environment,
                ["error"] = new JsonObject
                {
                    ["code"] = exception.Code,
                    ["message"] = exception.Message,
                    ["retryAt"] = exception.RetryAt?.ToString("O", CultureInfo.InvariantCulture)
                }
            };
            return new CallToolResult
            {
                IsError = true,
                StructuredContent = JsonSerializer.SerializeToElement(payload),
                Content = [new TextContentBlock { Text = payload.ToJsonString() }]
            };
        }
    }

    private static void ValidateTicker(string? ticker)
    {
        if (ticker is not null && (ticker.Length is < 1 or > 64 ||
            ticker.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_'))))
        {
            throw Input("ticker must be an exact Trading 212 identifier containing only letters, digits, and underscores.");
        }
    }

    private static void ValidatePaging(int limit, int offset)
    {
        if (limit is < 1 or > 50 || offset is < 0 or > 1_000_000)
        {
            throw Input("limit must be 1-50 and offset must be 0-1000000.");
        }
    }

    private static BrokerException Input(string message) => new("invalid_input", message);
}
