using Game.Core;

namespace Game.Client;

public interface IPlayerGameClient
{
    Task<CreateGameResult> CreateGameAsync(
        string name,
        int playerCount,
        CancellationToken cancellationToken);

    Task<JoinGameResult> JoinGameAsync(string name, CancellationToken cancellationToken);

    Task<GameSnapshot> GetStateAsync(Guid gameId, CancellationToken cancellationToken);

    Task<RollResult> RollAsync(
        Guid gameId,
        Guid playerId,
        CancellationToken cancellationToken);
}
