using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Trading212Mcp.Server.Configuration;

namespace Trading212Mcp.Server.Trading212;

public sealed record BrokerData(JsonNode Data, DateTimeOffset RetrievedAt, bool Cached);

public sealed class Trading212Client(
    IHttpClientFactory httpClientFactory,
    ServerSettings settings,
    TimeProvider clock,
    ILogger<Trading212Client> logger)
{
    private const int MaxResponseBytes = 16 * 1024 * 1024;
    private readonly Dictionary<string, Bucket> buckets = new(StringComparer.Ordinal);
    private readonly object bucketsLock = new();

    public async Task<BrokerData> GetAsync(string path, CancellationToken cancellationToken)
    {
        var policy = ReadPolicy.Validate(HttpMethod.Get, path);
        Bucket bucket;
        lock (bucketsLock)
        {
            if (!buckets.TryGetValue(policy.Bucket, out bucket!))
            {
                bucket = new Bucket(policy.Limit, policy.Period);
                buckets.Add(policy.Bucket, bucket);
            }
        }

        await bucket.Gate.WaitAsync(cancellationToken);
        try
        {
            var now = clock.GetUtcNow();
            if (bucket.Cache.TryGetValue(path, out var cached) && now - cached.RetrievedAt < policy.CacheTtl)
            {
                return cached with { Data = cached.Data.DeepClone(), Cached = true };
            }
            foreach (var key in bucket.Cache.Where(entry =>
                         now - entry.Value.RetrievedAt >= policy.CacheTtl).Select(entry => entry.Key).ToArray())
            {
                bucket.Cache.Remove(key);
            }

            bucket.Requests.RemoveAll(time => now - time >= bucket.Period);
            if (now >= bucket.HeaderReset)
            {
                bucket.Remaining = null;
            }
            var blockedUntil = bucket.BlockedUntil;
            if (bucket.Remaining == 0 && bucket.HeaderReset > blockedUntil)
            {
                blockedUntil = bucket.HeaderReset;
            }
            if (bucket.Requests.Count >= bucket.Limit)
            {
                var localReset = bucket.Requests[0] + bucket.Period;
                if (localReset > blockedUntil)
                {
                    blockedUntil = localReset;
                }
            }
            if (blockedUntil > now)
            {
                throw new BrokerException("rate_limited", "Account endpoint allowance is exhausted. Try after retryAt.", blockedUntil);
            }
            bucket.Requests.Add(now);
            if (bucket.Remaining.HasValue)
            {
                bucket.Remaining--;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(settings.BrokerOrigin, path));
            request.Headers.TryAddWithoutValidation("Authorization", settings.BrokerAuthorization);
            using var client = httpClientFactory.CreateClient("broker");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            UpdateBudget(bucket, response, clock.GetUtcNow());
            if (!response.IsSuccessStatusCode)
            {
                throw MapError(response.StatusCode, bucket.BlockedUntil > now ? bucket.BlockedUntil : null);
            }
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
            {
                throw new BrokerException("invalid_response", "Broker response exceeded the permitted size.");
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[8192];
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(chunk, timeout.Token)) != 0)
            {
                if (buffer.Length + bytesRead > MaxResponseBytes)
                {
                    throw new BrokerException("invalid_response", "Broker response exceeded the permitted size.");
                }
                await buffer.WriteAsync(chunk.AsMemory(0, bytesRead), timeout.Token);
            }
            var data = JsonNode.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length))
                ?? throw new BrokerException("invalid_response", "Broker returned an empty JSON value.");
            ValidateShape(data, policy.Shape);
            var result = new BrokerData(data, clock.GetUtcNow(), false);
            if (policy.CacheTtl > TimeSpan.Zero)
            {
                bucket.Cache[path] = result with { Data = data.DeepClone() };
            }
            return result;
        }
        catch (HttpRequestException)
        {
            logger.LogWarning("Broker read failed: upstream_unavailable.");
            throw new BrokerException("upstream_unavailable", "Trading 212 could not be reached.");
        }
        catch (IOException)
        {
            logger.LogWarning("Broker read failed: upstream_unavailable.");
            throw new BrokerException("upstream_unavailable", "Trading 212 response could not be read.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Broker read failed: upstream_timeout.");
            throw new BrokerException("upstream_timeout", "Trading 212 read timed out.");
        }
        catch (JsonException)
        {
            logger.LogWarning("Broker read failed: invalid_response.");
            throw new BrokerException("invalid_response", "Trading 212 returned malformed JSON.");
        }
        finally
        {
            bucket.Gate.Release();
        }
    }

    private static void ValidateShape(JsonNode data, string shape)
    {
        var valid = shape switch
        {
            "summary" => data is JsonObject summary && summary["currency"] is JsonValue &&
                         summary["cash"] is JsonObject && summary["investments"] is JsonObject,
            "array" => data is JsonArray array && array.All(item => item is JsonObject),
            "object" => data is JsonObject,
            "page" => data is JsonObject page && page["items"] is JsonArray items &&
                      items.All(item => item is JsonObject) && page.ContainsKey("nextPagePath") &&
                      (page["nextPagePath"] is null || page["nextPagePath"] is JsonValue value &&
                          value.TryGetValue<string>(out _)),
            _ => false
        };
        if (!valid)
        {
            throw new BrokerException("invalid_response", "Trading 212 returned an unexpected response shape.");
        }
    }

    private static BrokerException MapError(HttpStatusCode status, DateTimeOffset? retryAt) => status switch
    {
        HttpStatusCode.BadRequest => new("invalid_input", "Trading 212 rejected the read filters."),
        HttpStatusCode.Unauthorized => new("broker_unauthorized", "Trading 212 rejected the server's credentials."),
        HttpStatusCode.Forbidden => new("broker_forbidden", "Check the server key's read scopes and IP restrictions."),
        HttpStatusCode.NotFound => new("not_found", "Pending order was not found; this does not prove it was cancelled or unfilled."),
        HttpStatusCode.TooManyRequests => new("rate_limited", "Trading 212 account allowance is exhausted.", retryAt),
        HttpStatusCode.RequestTimeout => new("upstream_timeout", "Trading 212 read timed out."),
        _ => new("upstream_error", $"Trading 212 returned HTTP {(int)status}.")
    };

    private static void UpdateBudget(Bucket bucket, HttpResponseMessage response, DateTimeOffset now)
    {
        if (HeaderNumber(response, "x-ratelimit-limit") is > 0 and <= 10000 and var limit)
        {
            bucket.Limit = Math.Min(bucket.Limit, (int)limit);
        }
        if (HeaderNumber(response, "x-ratelimit-period") is > 0 and <= 86400 and var period)
        {
            bucket.Period = TimeSpan.FromSeconds(Math.Max(bucket.Period.TotalSeconds, period));
        }
        var reset = HeaderNumber(response, "x-ratelimit-reset");
        bucket.HeaderReset = reset is > 0 and < 253402300799
            ? DateTimeOffset.FromUnixTimeSeconds(reset.Value)
            : now + bucket.Period;
        var remaining = HeaderNumber(response, "x-ratelimit-remaining");
        var used = HeaderNumber(response, "x-ratelimit-used");
        if (remaining is >= 0 and <= 10000)
        {
            bucket.Remaining = (int)remaining.Value;
        }
        if (used is >= 0 and <= 10000)
        {
            var inferredRemaining = Math.Max(0, bucket.Limit - (int)used.Value);
            bucket.Remaining = Math.Min(bucket.Remaining ?? inferredRemaining, inferredRemaining);
        }
        if (HeaderNumber(response, "x-ratelimit-remaining") == 0 ||
            response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            bucket.BlockedUntil = bucket.HeaderReset > now ? bucket.HeaderReset : now + bucket.Period;
        }
        if (response.Headers.RetryAfter is { } retry)
        {
            var retryAt = retry.Date ?? now + (retry.Delta ?? TimeSpan.Zero);
            if (retryAt > bucket.BlockedUntil)
            {
                bucket.BlockedUntil = retryAt;
            }
        }
    }

    private static long? HeaderNumber(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) &&
        long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number : null;

    private sealed class Bucket(int limit, TimeSpan period)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Dictionary<string, BrokerData> Cache { get; } = new(StringComparer.Ordinal);
        public List<DateTimeOffset> Requests { get; } = [];
        public int Limit { get; set; } = limit;
        public TimeSpan Period { get; set; } = period;
        public DateTimeOffset BlockedUntil { get; set; }
        public DateTimeOffset HeaderReset { get; set; }
        public int? Remaining { get; set; }
    }
}
