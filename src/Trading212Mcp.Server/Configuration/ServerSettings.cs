using System.Security.Cryptography;
using System.Text;

namespace Trading212Mcp.Server.Configuration;

public sealed record ServerSettings(
    string Environment,
    Uri BrokerOrigin,
    string BrokerAuthorization,
    byte[] TokenHash,
    IReadOnlySet<string> AllowedOrigins)
{
    public static ServerSettings Load(IConfiguration configuration)
    {
        var environment = configuration["Trading212:Environment"];
        var origin = environment switch
        {
            "Live" => new Uri("https://live.trading212.com"),
            "Demo" => new Uri("https://demo.trading212.com"),
            _ => throw new InvalidOperationException("Trading212:Environment must explicitly be Live or Demo.")
        };

        var hosts = configuration["AllowedHosts"];
        if (string.IsNullOrWhiteSpace(hosts) || hosts.Split(';').Any(host =>
                string.IsNullOrWhiteSpace(host) || host.Contains('*')))
        {
            throw new InvalidOperationException("AllowedHosts must specify explicit server hostnames.");
        }

        var key = ReadSecret(configuration, "Trading212:ApiKeyFile");
        var secret = ReadSecret(configuration, "Trading212:ApiSecretFile");
        if (key.Contains(':') || key.Any(char.IsControl) || secret.Any(char.IsControl))
        {
            throw new InvalidOperationException("Broker credential files contain invalid characters.");
        }

        var token = ReadSecret(configuration, "Mcp:AccessTokenFile");
        if (token.Length < 32 || token.Length > 512 || token.Any(char.IsWhiteSpace))
        {
            throw new InvalidOperationException("MCP access token must contain 32-512 non-whitespace characters.");
        }

        var allowedOrigins = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in (configuration["Mcp:AllowedOrigins"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                uri.Scheme != Uri.UriSchemeHttps || uri.AbsolutePath != "/" ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
                !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new InvalidOperationException("Mcp:AllowedOrigins must contain HTTPS origins only.");
            }
            allowedOrigins.Add(uri.GetLeftPart(UriPartial.Authority));
        }

        return new ServerSettings(environment!, origin,
            "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{key}:{secret}")),
            SHA256.HashData(Encoding.UTF8.GetBytes(token)), allowedOrigins);
    }

    private static string ReadSecret(IConfiguration configuration, string setting)
    {
        var path = configuration[setting];
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException($"{setting} must specify an absolute secret file path.");
        }

        string value;
        try
        {
            if (OperatingSystem.IsLinux() && (File.GetUnixFileMode(path) &
                (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                 UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            {
                throw new InvalidOperationException($"{setting} must use owner-only file permissions.");
            }
            value = File.ReadAllText(path).TrimEnd('\r', '\n');
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{setting} could not be read.");
        }

        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096)
        {
            throw new InvalidOperationException($"{setting} must contain a non-empty credential of at most 4096 characters.");
        }
        return value;
    }
}
