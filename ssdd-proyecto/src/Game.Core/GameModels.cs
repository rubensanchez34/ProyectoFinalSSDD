using System.Collections.Concurrent;
using System.Collections.ObjectModel;

namespace Game.Core;

public enum GameStatus
{
    WaitingForPlayers,
    InProgress,
    Finished
}

public enum GameError
{
    InvalidName,
    InvalidPlayerCount,
    GameAlreadyActive,
    GameNotFound,
    GameFull,
    GameNotStarted,
    GameFinished,
    NotPlayersTurn
}

public sealed class GameRuleException(GameError error, string message) : Exception(message)
{
    public GameError Error { get; } = error;
}


public sealed class GameEventDeliveryException(string message, Exception innerException)
    : Exception(message, innerException)
{
}


public sealed record PlayerSnapshot(Guid Id, int Number, int Score);

public sealed record GameSnapshot(
    Guid Id,
    string Name,
    int Capacity,
    GameStatus Status,
    IReadOnlyList<PlayerSnapshot> Players,
    Guid? CurrentPlayerId,
    Guid? WinnerId);

public sealed record CreateGameResult(Guid GameId, Guid PlayerId, GameSnapshot State);

public sealed record JoinGameResult(Guid PlayerId, GameSnapshot State, string? EventDeliveryWarning = null);

public sealed record RollResult(
    Guid PlayerId,
    int FirstDie,
    int SecondDie,
    GameSnapshot State,
    string? EventDeliveryWarning = null);

public sealed record PlayerScore(Guid PlayerId, int PlayerNumber, int Score);

public abstract record GameEvent(Guid GameId);

public sealed record TurnChangedEvent(Guid GameId, int? CurrentPlayerNumber, int? WinnerPlayerNumber)
    : GameEvent(GameId);

public sealed record ScoresUpdatedEvent(
    Guid GameId,
    IReadOnlyList<PlayerScore> Scores,
    Guid? WinnerId)
    : GameEvent(GameId);

public interface IDiceRoller
{
    (int First, int Second) Roll();
}

public sealed class RandomDiceRoller : IDiceRoller
{
    public (int First, int Second) Roll() =>
        (Random.Shared.Next(1, 7), Random.Shared.Next(1, 7));
}

public interface IGameEventPublisher
{
    ValueTask PublishAsync(GameEvent gameEvent, CancellationToken cancellationToken);
}

internal sealed class GameState(
    Guid id,
    string name,
    int capacity,
    PlayerState creator)
{
    private TaskCompletionSource _turnChanged =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public int Capacity { get; } = capacity;
    public GameStatus Status { get; set; } = GameStatus.WaitingForPlayers;
    public List<PlayerState> Players { get; } = [creator];
    public Dictionary<Guid, PlayerState> PlayersById { get; } = new() { [creator.Id] = creator };
    public ConcurrentQueue<PlayerState> TurnOrder { get; } = new([creator]);
    public Guid? WinnerId { get; set; }
    public Task TurnChanged => _turnChanged.Task;

    public void NotifyTurnChanged()
    {
        var previousTurnChanged = _turnChanged;
        _turnChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previousTurnChanged.TrySetResult();
    }
}

internal sealed class PlayerState(Guid id, int number)
{
    public Guid Id { get; } = id;
    public int Number { get; } = number;
    public int Score { get; set; }
}

internal static class GameSnapshots
{
    public static GameSnapshot Create(GameState game)
    {
        game.TurnOrder.TryPeek(out var currentPlayer);
        var players = game.Players
            .Select(player => new PlayerSnapshot(player.Id, player.Number, player.Score))
            .ToArray();

        return new GameSnapshot(
            game.Id,
            game.Name,
            game.Capacity,
            game.Status,
            new ReadOnlyCollection<PlayerSnapshot>(players),
            game.Status == GameStatus.InProgress ? currentPlayer?.Id : null,
            game.WinnerId);
    }

    public static IReadOnlyList<PlayerScore> CreateScores(GameState game) =>
        new ReadOnlyCollection<PlayerScore>(
            game.Players
                .Select(player => new PlayerScore(player.Id, player.Number, player.Score))
                .ToArray());
}
