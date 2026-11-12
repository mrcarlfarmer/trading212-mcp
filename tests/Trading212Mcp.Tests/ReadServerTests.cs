using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Trading212Mcp.Server.Configuration;
using Trading212Mcp.Server.Tools;
using Trading212Mcp.Server.Trading212;
using Trading212Mcp.Bridge;

namespace Trading212Mcp.Tests;

public sealed class ReadServerTests
{
    [Theory]
    [InlineData("POST", "/api/v0/equity/orders/market")]
    [InlineData("DELETE", "/api/v0/equity/orders/12")]
    [InlineData("GET", "/api/v0/equity/history/exports")]
    [InlineData("GET", "/api/v0/equity/../orders")]
    [InlineData("GET", "/api/v0/equity/orders/%31")]
    [InlineData("GET", "//evil.example/api/v0/equity/orders")]
    [InlineData("GET", "https://evil.example/api/v0/equity/orders")]
    [InlineData("GET", "/api/v0/equity/history/orders?limit=51")]
    [InlineData("GET", "/api/v0/equity/history/orders?limit=20&limit=10")]
    [InlineData("GET", "/api/v0/equity/orders?execute=true")]
    public void BrokerPolicyRejectsWritesAndPathBypasses(string method, string path) =>
        Assert.Throws<BrokerException>(() => ReadPolicy.Validate(new HttpMethod(method), path));

    [Theory]
    [InlineData(null)]
    [InlineData("live")]
    [InlineData("Invalid")]
    public void EnvironmentMustBeExplicit(string? environment)
    {
        using var host = new TestHost();
        host.Settings["Trading212:Environment"] = environment;
        var config = new ConfigurationBuilder().AddInMemoryCollection(host.Settings).Build();
        var error = Assert.Throws<InvalidOperationException>(() => ServerSettings.Load(config));
        Assert.DoesNotContain(TestHost.ApiSecret, error.Message);
    }

    [Fact]
    public void MissingOrEmptySecretFileFailsWithoutLeakingValues()
    {
        using var host = new TestHost();
        host.Settings["Trading212:ApiSecretFile"] = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var config = new ConfigurationBuilder().AddInMemoryCollection(host.Settings).Build();
        var error = Assert.Throws<InvalidOperationException>(() => ServerSettings.Load(config));
        Assert.Equal("Trading212:ApiSecretFile could not be read.", error.Message);
        File.WriteAllText(host.Settings["Trading212:ApiKeyFile"]!, "");
        host.Settings["Trading212:ApiSecretFile"] = host.Settings["Trading212:ApiKeyFile"];
        config = new ConfigurationBuilder().AddInMemoryCollection(host.Settings).Build();
        Assert.Throws<InvalidOperationException>(() => ServerSettings.Load(config));
    }

    [Fact]
    public async Task LiveReadsUseServerCredentialsPreserveDecimalsAndStringifyIds()
    {
        using var host = new TestHost();
        var tool = host.Services.GetRequiredService<ReadTools>();
        var result = await tool.AccountSummary(default);
        var payload = Payload(result);
        Assert.Equal("Live", payload["environment"]!.GetValue<string>());
        Assert.Equal("9223372036854775807", payload["data"]!["id"]!.GetValue<string>());
        Assert.Equal(100.25m, payload["data"]!["cash"]!["availableToTrade"]!.GetValue<decimal>());
        var request = Assert.Single(host.Handler.Requests);
        Assert.Equal("live.trading212.com", request.Uri!.Host);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{TestHost.ApiKey}:{TestHost.ApiSecret}")),
            request.Authorization);
        Assert.DoesNotContain(TestHost.ApiKey, result.Content.OfType<TextContentBlock>().Single().Text);
        Assert.DoesNotContain(TestHost.ApiSecret, result.Content.OfType<TextContentBlock>().Single().Text);
    }

    [Fact]
    public async Task CurrencySeparationAndLocalPagingArePreserved()
    {
        using var host = new TestHost();
        host.Handler.Response = (_, _) => Task.FromResult(StubHandler.Json("""
            [{"instrument":{"ticker":"A_US_EQ","currency":"USD"},"quantity":0.123456789123456789,
              "currentPrice":10.25,"walletImpact":{"currency":"GBP","currentValue":8.25}},
             {"instrument":{"ticker":"B_US_EQ","currency":"USD"},"quantity":2}]
            """));
        var result = await host.Services.GetRequiredService<ReadTools>().Positions(default, limit: 1);
        var page = Payload(result)["data"]!;
        Assert.Equal(2, page["total"]!.GetValue<int>());
        Assert.Equal(1, page["nextOffset"]!.GetValue<int>());
        var position = page["items"]![0]!;
        Assert.Equal("USD", position["instrument"]!["currency"]!.GetValue<string>());
        Assert.Equal("GBP", position["walletImpact"]!["currency"]!.GetValue<string>());
        Assert.Equal(0.123456789123456789m, position["quantity"]!.GetValue<decimal>());
    }

    [Fact]
    public async Task ConcurrentAccountReadsCoalesceAndExpiredCacheDoesNotHideFailure()
    {
        using var host = new TestHost();
        var broker = host.Services.GetRequiredService<Trading212Client>();
        var reads = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => broker.GetAsync(ReadPolicy.Prefix + "account/summary", default)));
        Assert.Single(host.Handler.Requests);
        Assert.Equal(7, reads.Count(result => result.Cached));
        host.Clock.Advance(TimeSpan.FromSeconds(6));
        host.Handler.Response = (_, _) => throw new HttpRequestException("synthetic secret must not reach output");
        var error = await Assert.ThrowsAsync<BrokerException>(() =>
            broker.GetAsync(ReadPolicy.Prefix + "account/summary", default));
        Assert.Equal("upstream_unavailable", error.Code);
        Assert.DoesNotContain("synthetic secret", error.Message);
    }

    [Fact]
    public async Task RateBudgetIsSharedAcrossTickerFilters()
    {
        using var host = new TestHost();
        host.Handler.Response = (_, _) => Task.FromResult(StubHandler.Json("[]"));
        var broker = host.Services.GetRequiredService<Trading212Client>();
        await broker.GetAsync(ReadPolicy.Prefix + "positions?ticker=A_US_EQ", default);
        var error = await Assert.ThrowsAsync<BrokerException>(() =>
            broker.GetAsync(ReadPolicy.Prefix + "positions?ticker=B_US_EQ", default));
        Assert.Equal("rate_limited", error.Code);
        Assert.Single(host.Handler.Requests);
        host.Clock.Advance(TimeSpan.FromSeconds(1));
        await broker.GetAsync(ReadPolicy.Prefix + "positions?ticker=B_US_EQ", default);
        Assert.Equal(2, host.Handler.Requests.Count);
    }

    [Fact]
    public async Task UpstreamRateResetPreventsRequestsAndNeverReturnsStaleData()
    {
        using var host = new TestHost();
        host.Handler.Response = (_, _) =>
        {
            var response = StubHandler.Json(TestHost.ApiSecret, HttpStatusCode.TooManyRequests);
            response.Headers.Add("x-ratelimit-remaining", "0");
            response.Headers.Add("x-ratelimit-reset", (host.Clock.GetUtcNow().ToUnixTimeSeconds() + 120).ToString());
            return Task.FromResult(response);
        };
        var tools = host.Services.GetRequiredService<ReadTools>();
        var first = await tools.OrderHistory(default);
        var second = await tools.OrderHistory(default);
        Assert.True(first.IsError);
        Assert.Equal("rate_limited", Payload(second)["error"]!["code"]!.GetValue<string>());
        Assert.NotNull(Payload(second)["error"]!["retryAt"]);
        Assert.Single(host.Handler.Requests);
        Assert.DoesNotContain(TestHost.ApiSecret, first.Content.OfType<TextContentBlock>().Single().Text);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "broker_unauthorized")]
    [InlineData(HttpStatusCode.Forbidden, "broker_forbidden")]
    [InlineData(HttpStatusCode.NotFound, "not_found")]
    [InlineData(HttpStatusCode.RequestTimeout, "upstream_timeout")]
    [InlineData(HttpStatusCode.Redirect, "upstream_error")]
    public async Task BrokerErrorsAreSanitizedAndNeverFallBackToDemo(HttpStatusCode status, string code)
    {
        using var host = new TestHost();
        host.Handler.Response = (_, _) => Task.FromResult(StubHandler.Json(TestHost.ApiSecret, status));
        var result = await host.Services.GetRequiredService<ReadTools>().AccountSummary(default);
        Assert.True(result.IsError);
        Assert.Equal(code, Payload(result)["error"]!["code"]!.GetValue<string>());
        Assert.DoesNotContain(TestHost.ApiSecret, result.Content.OfType<TextContentBlock>().Single().Text);
        Assert.Equal("live.trading212.com", Assert.Single(host.Handler.Requests).Uri!.Host);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"currency":"GBP"}""")]
    public async Task MalformedBrokerResponsesAreNotSuccessShaped(string json)
    {
        using var host = new TestHost();
        host.Handler.Response = (_, _) => Task.FromResult(StubHandler.Json(json));
        var result = await host.Services.GetRequiredService<ReadTools>().AccountSummary(default);
        Assert.True(result.IsError);
        Assert.Equal("invalid_response", Payload(result)["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task HistoryContinuationPreservesFullPathAndTermination()
    {
        using var host = new TestHost();
        host.Handler.Response = (request, _) => Task.FromResult(StubHandler.Json(
            request.RequestUri!.Query.Contains("cursor", StringComparison.Ordinal)
                ? """{"items":[],"nextPagePath":null}"""
                : """{"items":[{"order":{"id":10}}],"nextPagePath":"/api/v0/equity/history/orders?limit=2&cursor=1760346100000&ticker=A_US_EQ"}"""));
        var tools = host.Services.GetRequiredService<ReadTools>();
        var first = Payload(await tools.OrderHistory(default, limit: 2, ticker: "A_US_EQ"));
        var token = first["data"]!["continuationToken"]!.GetValue<string>();
        var next = Payload(await tools.OrderHistory(default, continuationToken: token));
        Assert.False(next["data"]!["hasMore"]!.GetValue<bool>());
        Assert.Null(next["data"]!["continuationToken"]);
        Assert.Equal("?limit=2&cursor=1760346100000&ticker=A_US_EQ", host.Handler.Requests[1].Uri!.Query);
    }

    [Theory]
    [InlineData("https://evil.example/api/v0/equity/history/orders")]
    [InlineData("/api/v0/equity/history/transactions?limit=20")]
    [InlineData("/api/v0/equity/history/orders?limit=100")]
    public async Task ForgedContinuationCannotChangeHostEndpointOrLimits(string path)
    {
        using var host = new TestHost();
        var token = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(path));
        var result = await host.Services.GetRequiredService<ReadTools>().OrderHistory(default, continuationToken: token);
        Assert.True(result.IsError);
        Assert.Empty(host.Handler.Requests);
    }

    [Fact]
    public async Task UnsafeUpstreamContinuationIsAnExplicitBrokerError()
    {
        using var host = new TestHost();
        host.Handler.Response = (_, _) => Task.FromResult(StubHandler.Json("""
            {"items":[],"nextPagePath":"https://evil.example/api/v0/equity/history/orders"}
            """));
        var result = await host.Services.GetRequiredService<ReadTools>().OrderHistory(default);
        Assert.True(result.IsError);
        Assert.Equal("invalid_response", Payload(result)["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task CancellationReachesBrokerAndDoesNotBecomeSuccess()
    {
        using var host = new TestHost();
        host.Handler.Response = async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return StubHandler.Json("{}");
        };
        using var ct = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            host.Services.GetRequiredService<ReadTools>().AccountSummary(ct.Token));
    }

    [Fact]
    public async Task HttpTransportAuthenticatesAllMcpRequestsAndChecksHostAndOrigin()
    {
        using var host = new TestHost();
        using var http = host.CreateClient();
        using var unauthenticated = await http.PostAsync("/mcp", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthenticated.StatusCode);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestHost.Token);
        http.DefaultRequestHeaders.Add("Origin", "https://evil.example");
        using var untrustedOrigin = await http.PostAsync("/mcp", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Forbidden, untrustedOrigin.StatusCode);
        http.DefaultRequestHeaders.Remove("Origin");
        http.DefaultRequestHeaders.Host = "evil.example";
        using var untrustedHost = await http.PostAsync("/mcp", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, untrustedHost.StatusCode);
        Assert.Empty(host.Handler.Requests);
    }

    [Fact]
    public async Task OfficialMcpClientDiscoversOnlyReadToolsAndReceivesStructuredResults()
    {
        using var host = new TestHost();
        using var http = host.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestHost.Token);
        await using var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp
        }, http);
        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();
        Assert.Equal(10, tools.Count);
        Assert.All(tools, tool => Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint));
        Assert.DoesNotContain(tools, tool => tool.Name.Contains("place", StringComparison.Ordinal) ||
                                            tool.Name.Contains("cancel", StringComparison.Ordinal));
        var result = await client.CallToolAsync("get_account_summary");
        Assert.False(result.IsError == true);
        Assert.Equal("Live", result.StructuredContent!.Value.GetProperty("environment").GetString());
        var bridge = new ReadOnlyBridge(client);
        var forwardedTools = await bridge.ListAsync(default);
        Assert.Equal(10, forwardedTools.Tools.Count);
        var forwarded = await bridge.CallAsync(new CallToolRequestParams { Name = "get_account_summary" }, default);
        Assert.Equal(result.StructuredContent.Value.GetProperty("data").GetRawText(),
            forwarded.StructuredContent!.Value.GetProperty("data").GetRawText());
        var blocked = await bridge.CallAsync(new CallToolRequestParams { Name = "place_market_order" }, default);
        Assert.True(blocked.IsError);
        Assert.Single(host.Handler.Requests);
    }

    [Fact]
    public async Task BrokerCannotExceedRequestedHistoryPageSize()
    {
        using var host = new TestHost();
        host.Handler.Response = (_, _) => Task.FromResult(StubHandler.Json("""
            {"items":[{},{}],"nextPagePath":null}
            """));
        var result = await host.Services.GetRequiredService<ReadTools>().OrderHistory(default, limit: 1);
        Assert.True(result.IsError);
        Assert.Equal("invalid_response", Payload(result)["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task PositiveRemainingAndUsedHeadersLimitOtherApplicationsAllowance()
    {
        using var host = new TestHost();
        host.Handler.Response = (_, _) =>
        {
            var response = StubHandler.Json("""{"items":[],"nextPagePath":null}""");
            var second = host.Handler.Requests.Count > 1;
            response.Headers.Add("x-ratelimit-remaining", second ? "0" : "1");
            response.Headers.Add("x-ratelimit-used", second ? "20" : "19");
            response.Headers.Add("x-ratelimit-reset", (host.Clock.GetUtcNow().ToUnixTimeSeconds() + 60).ToString());
            return Task.FromResult(response);
        };
        var tools = host.Services.GetRequiredService<ReadTools>();
        Assert.False((await tools.OrderHistory(default)).IsError == true);
        Assert.False((await tools.OrderHistory(default)).IsError == true);
        var result = await tools.OrderHistory(default);
        Assert.True(result.IsError);
        Assert.Equal(2, host.Handler.Requests.Count);
    }

    [Fact]
    public async Task BridgeRejectsInsecureUrlWithProtocolOnlyStdout()
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.ChangeExtension(typeof(TestHost).Assembly.Location, ".runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.ChangeExtension(typeof(TestHost).Assembly.Location, ".deps.json"));
        start.ArgumentList.Add(typeof(ReadOnlyBridge).Assembly.Location);
        start.Environment["MCP_SERVER_URL"] = "http://localhost:8080/mcp";
        start.Environment["MCP_ACCESS_TOKEN_FILE"] = "not-an-absolute-file";
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(1, process.ExitCode);
        Assert.Empty(await stdout);
        Assert.Contains("HTTPS /mcp", await stderr);
    }

    private static JsonNode Payload(CallToolResult result) =>
        JsonNode.Parse(result.StructuredContent!.Value.GetRawText())!;
}
