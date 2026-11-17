namespace Game.Client;

public sealed class GameEventSubscriptionException(string message, Exception innerException)
    : Exception(message, innerException)
{
}
