using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;

namespace Trading212Mcp.Server.Trading212;

public sealed record EndpointPolicy(string Bucket, int Limit, TimeSpan Period, TimeSpan CacheTtl, string Shape);

public static class ReadPolicy
{
    public const string Prefix = "/api/v0/equity/";

    private static readonly Dictionary<string, EndpointPolicy> Endpoints = new(StringComparer.Ordinal)
    {
        ["account/summary"] = new("account/summary", 1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), "summary"),
        ["positions"] = new("positions", 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), "array"),
        ["metadata/instruments"] = new("metadata/instruments", 1, TimeSpan.FromSeconds(50), TimeSpan.FromMinutes(10), "array"),
        ["metadata/exchanges"] = new("metadata/exchanges", 1, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), "array"),
        ["orders"] = new("orders", 1, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), "array"),
        ["history/orders"] = new("history/orders", 20, TimeSpan.FromMinutes(1), TimeSpan.Zero, "page"),
        ["history/dividends"] = new("history/dividends", 20, TimeSpan.FromMinutes(1), TimeSpan.Zero, "page"),
        ["history/transactions"] = new("history/transactions", 20, TimeSpan.FromMinutes(1), TimeSpan.Zero, "page")
    };

    public static EndpointPolicy Validate(HttpMethod method, string path)
    {
        if (method != HttpMethod.Get || !path.StartsWith(Prefix, StringComparison.Ordinal) ||
            path.Contains('\\') || path.Contains('#') || path.Any(char.IsControl))
        {
            throw Invalid();
        }

        var parts = path.Split('?', 2);
        if (parts[0].Contains('%'))
        {
            throw Invalid();
        }
        var endpoint = parts[0][Prefix.Length..];
        EndpointPolicy policy;
        if (Endpoints.TryGetValue(endpoint, out var found))
        {
            policy = found;
        }
        else if (endpoint.StartsWith("orders/", StringComparison.Ordinal) &&
                 long.TryParse(endpoint[7..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            policy = new("orders/{id}", 1, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), "object");
        }
        else
        {
            throw Invalid();
        }

        if (parts.Length == 2)
        {
            ValidateQuery(endpoint, parts[1]);
        }
        return policy;
    }

    private static void ValidateQuery(string endpoint, string query)
    {
        var allowed = endpoint switch
        {
            "positions" => new[] { "ticker" },
            "history/orders" or "history/dividends" => ["limit", "cursor", "ticker"],
            "history/transactions" => ["limit", "cursor", "time"],
            _ => []
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (query.Length == 0 || query.Length > 2048)
        {
            throw Invalid();
        }
        foreach (var item in QueryHelpers.ParseQuery(query))
        {
            var name = item.Key;
            var value = item.Value.ToString();
            if (!allowed.Contains(name) || !seen.Add(name) || item.Value.Count != 1 ||
                value.Length == 0 || value.Length > 128 || value.Any(char.IsControl))
            {
                throw Invalid();
            }
            if (name == "limit" && (!int.TryParse(value, out var limit) || limit is < 1 or > 50))
            {
                throw Invalid();
            }
            if (name == "ticker" && value.Any(character =>
                    !(char.IsAsciiLetterOrDigit(character) || character == '_')))
            {
                throw Invalid();
            }
            if (name == "cursor" && endpoint != "history/transactions" &&
                !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                throw Invalid();
            }
            if (name == "time" && !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out _))
            {
                throw Invalid();
            }
        }
    }

    private static BrokerException Invalid() => new("invalid_input", "Unsupported broker method, path, or query.");
}
