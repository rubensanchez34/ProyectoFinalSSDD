using Grpc.Core;

namespace Game.Client;

public sealed class GameClientException(StatusCode statusCode, string message, Exception innerException)
    : Exception(message, innerException)
{
    public StatusCode StatusCode { get; } = statusCode;
}
