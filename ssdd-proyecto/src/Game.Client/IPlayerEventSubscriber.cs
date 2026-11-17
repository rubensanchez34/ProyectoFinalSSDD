using Game.Core;

namespace Game.Client;

public interface IPlayerEventSubscriber
{
    Task StartAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<GameEvent> ReadEventsAsync(CancellationToken cancellationToken);
}
