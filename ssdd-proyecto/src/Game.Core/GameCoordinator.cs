namespace Game.Core;

public sealed class GameCoordinator
{
    private static readonly TimeSpan SimulatedRollDelay = TimeSpan.FromSeconds(2);
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly IDiceRoller _diceRoller;
    private readonly IGameEventPublisher _eventPublisher;
    private GameState? _game;

    public GameCoordinator(IDiceRoller diceRoller, IGameEventPublisher eventPublisher)
    {
        // a completar asignación de _diceRoller y _eventPublisher
    }

    public async Task<CreateGameResult> CreateGameAsync(
        string name,
        int playerCount,
        CancellationToken cancellationToken = default)
    {
        /*
        1. Espera el acceso a la operación. Adquiere el semáforo _operationGate de forma asíncrona con WaitAsync, pasando cancellationToken.
        2. Protege el estado compartido. Una vez adquirido el semáforo, usa un bloque try y, dentro de él, entra en un lock sobre _stateLock para que las comprobaciones y la creación se hagan de forma coordinada.
        3. Valida el nombre. Si name es nulo, está vacío o solo contiene espacios, lanza GameRuleException con GameError.InvalidName y el mensaje «El nombre de la partida es obligatorio.».
        4. Valida el número de jugadores. Si playerCount es menor que 2 o mayor que 4, lanza GameRuleException con GameError.InvalidPlayerCount y el mensaje «La partida debe tener entre 2 y 4 jugadores.».
        5. Comprueba si ya hay una partida activa. Si _game existe y su estado no es Finished, lanza GameRuleException con GameError.GameAlreadyActive y el mensaje «Ya hay una partida activa.».
        6. Crea el jugador inicial. Construye un PlayerState con un GUID nuevo y el número de jugador 1.
        7. Crea la partida. Construye un GameState con otro GUID nuevo, el nombre sin espacios al principio ni al final, el número de jugadores y el jugador inicial. Guarda la partida en _game.
        8. Prepara y devuelve el resultado. Crea una instantánea con GameSnapshots.Create(_game) y devuelve un CreateGameResult con el identificador de partida, el identificador del jugador y la instantánea, en ese orden.
        9. Libera el semáforo. En el bloque finally, llama a _operationGate.Release() para liberarlo tanto si la operación termina correctamente como si se produce una excepción.
        */
    }

    public async Task<JoinGameResult> JoinGameAsync(
        string gameName,
        CancellationToken cancellationToken = default)
    {
        /*
        1. Espera el acceso a la operación. Adquiere _operationGate de forma asíncrona con WaitAsync, pasando cancellationToken.
        */
        try{
        /*
        2. Prepara las variables. Dentro de un bloque try, declara una variable result de tipo JoinGameResult y una lista de solo lectura events de tipo GameEvent.
        3. Protege el estado compartido. Entra en un lock sobre _stateLock para consultar y modificar la partida de forma coordinada.
        4. Busca y comprueba la partida. Obtén la partida por nombre mediante GetGameByName(gameName). Si su estado no es WaitingForPlayers, lanza GameRuleException con GameError.GameFull y el mensaje «La partida ya no admite jugadores.».
        5. Añade el jugador. Crea un PlayerState con un GUID nuevo y el número correspondiente a la cantidad actual de jugadores más uno. Después:
            Añádelo a game.Players.
            Regístralo en game.PlayersById usando su identificador como clave.
            Incorpóralo a la cola game.TurnOrder.
        6. Comprueba si se han ocupado todas las plazas.
            Si el número de jugadores coincide con game.Capacity, cambia el estado a InProgress y llama a game.NotifyTurnChanged().
            Prepara en events un TurnChangedEvent con el identificador de partida, el número del primer jugador de la cola —o null si no existe— y null como último argumento. Añade también un ScoresUpdatedEvent con el identificador de partida, las puntuaciones obtenidas mediante GameSnapshots.CreateScores(game) y null como último argumento.
            Si todavía quedan plazas, asigna una lista vacía a events.
        7. Prepara el resultado. Crea un JoinGameResult con el identificador del nuevo jugador y una instantánea obtenida mediante GameSnapshots.Create(game); guárdalo en result.
        */

            
            var deliveryWarning = await PublishEventsAsync(events, cancellationToken);
            return result with { EventDeliveryWarning = deliveryWarning };
            
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public GameSnapshot GetState(Guid gameId)
    {
        lock (_stateLock)
        {
            var game = GetGameById(gameId);
            return GameSnapshots.Create(game);
        }
    }

    public async Task<RollResult> RollAsync(
        Guid gameId,
        Guid playerId,
        CancellationToken cancellationToken = default)
    {
        var waitedForTurn = false;
        while (true)
        {
            await _operationGate.WaitAsync(cancellationToken);
            Task? turnChanged = null;
            GameState? gameToRoll = null;
            PlayerState? playerToRoll = null;
            try
            {
                lock (_stateLock)
                {
                    var game = GetGameById(gameId);
                    if (game.Status == GameStatus.Finished)
                    {
                        throw new GameRuleException(GameError.GameFinished, "La partida ya ha finalizado.");
                    }

                    if (!game.PlayersById.ContainsKey(playerId))
                    {
                        throw new GameRuleException(GameError.NotPlayersTurn, "El jugador no pertenece a esta partida.");
                    }

                    if (game.Status == GameStatus.WaitingForPlayers)
                    {
                        turnChanged = game.TurnChanged;
                    }
                    else if (game.Status != GameStatus.InProgress)
                    {
                        throw new GameRuleException(GameError.GameNotStarted, "La partida todavía no ha comenzado.");
                    }
                    else if (!game.TurnOrder.TryPeek(out var currentPlayer))
                    {
                        throw new InvalidOperationException("La partida no tiene un jugador activo.");
                    }
                    else if (currentPlayer.Id != playerId)
                    {
                        turnChanged = game.TurnChanged;
                    }
                    else
                    {
                        gameToRoll = game;
                        playerToRoll = currentPlayer;
                    }
                }

                if (turnChanged is null)
                {
                    if (waitedForTurn)
                    {
                        // Simula un retraso en la tirada de dados para que el jugador vea el resultado de su tirada.
                        await Task.Delay(SimulatedRollDelay, cancellationToken);
                    }

                    var game = gameToRoll ??
                        throw new InvalidOperationException("No se encontró la partida para la tirada.");
                    var currentPlayer = playerToRoll ??
                        throw new InvalidOperationException("No se encontró el jugador activo para la tirada.");
                    RollResult result;
                    IReadOnlyList<GameEvent> events;
                    lock (_stateLock)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var (first, second) = _diceRoller.Roll();
                        if (first is < 1 or > 6 || second is < 1 or > 6)
                        {
                            throw new InvalidOperationException("El lanzador de dados debe devolver valores entre 1 y 6.");
                        }

                        currentPlayer.Score += first + second;
                        if (currentPlayer.Score >= 20)
                        {
                            game.Status = GameStatus.Finished;
                            game.WinnerId = currentPlayer.Id;
                            events =
                            [
                                new TurnChangedEvent(game.Id, null, currentPlayer.Number),
                                new ScoresUpdatedEvent(game.Id, GameSnapshots.CreateScores(game), currentPlayer.Id)
                            ];
                        }
                        else
                        {
                            if (!game.TurnOrder.TryDequeue(out var dequeued) || dequeued.Id != currentPlayer.Id)
                            {
                                throw new InvalidOperationException("El orden de turnos de la partida no es consistente.");
                            }

                            game.TurnOrder.Enqueue(currentPlayer);
                            game.TurnOrder.TryPeek(out var nextPlayer);
                            events =
                            [
                                new TurnChangedEvent(game.Id, nextPlayer?.Number, null),
                                new ScoresUpdatedEvent(game.Id, GameSnapshots.CreateScores(game), null)
                            ];
                        }

                        result = new RollResult(currentPlayer.Id, first, second, GameSnapshots.Create(game));
                        game.NotifyTurnChanged();
                    }

                    var deliveryWarning = await PublishEventsAsync(
                        events,
                        cancellationToken);
                    return result with
                    {
                        EventDeliveryWarning = deliveryWarning
                    };
                }
            }
            finally
            {
                _operationGate.Release();
            }

            await turnChanged.WaitAsync(cancellationToken);
            waitedForTurn = true;
        }
    }

    private async Task<string?> PublishEventsAsync(
        IReadOnlyList<GameEvent> events,
        CancellationToken cancellationToken)
    {
        var warnings = new List<string>();
        foreach (var gameEvent in events)
        {
            try
            {
                await _eventPublisher.PublishAsync(gameEvent, cancellationToken);
            }
            catch (GameEventDeliveryException exception)
            {
                warnings.Add(exception.Message);
            }
        }

        return warnings.Count == 0
            ? null
            : string.Join(" ", warnings.Distinct(StringComparer.Ordinal));
    }

    private GameState GetGameByName(string name)
    {
        if (_game is null || !string.Equals(_game.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new GameRuleException(GameError.GameNotFound, "No se encontró una partida activa con ese nombre.");
        }

        return _game;
    }

    private GameState GetGameById(Guid gameId)
    {
        if (_game is null || _game.Id != gameId)
        {
            throw new GameRuleException(GameError.GameNotFound, "No se encontró una partida con ese identificador.");
        }

        return _game;
    }
}
