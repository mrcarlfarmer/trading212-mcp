namespace Trading212Mcp.Server.Trading212;

public sealed class BrokerException(string code, string message, DateTimeOffset? retryAt = null)
    : Exception(message)
{
    public string Code { get; } = code;
    public DateTimeOffset? RetryAt { get; } = retryAt;
}
