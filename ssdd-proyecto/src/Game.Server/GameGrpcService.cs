using Game.Contracts;
using Game.Core;
using Grpc.Core;
using CoreGameStatus = Game.Core.GameStatus;
using ProtoGameStatus = Game.Contracts.GameStatus;

namespace Game.Server;

public sealed class GameGrpcService : GameService.GameServiceBase
{
    private readonly GameCoordinator _coordinator;

    public GameGrpcService(GameCoordinator coordinator)
    {
        // a completar asignación de _coordinator
    }

    public override async Task<GameReply> CreateGame(
        CreateGameRequest request,
        ServerCallContext context)
    {
        try
        {
            // llamar CreateGameAsync(request.Name, request.PlayerCount, context.CancellationToken)
            // convertir State con ToContract y devolver State + PlayerId
        }  
        catch (GameRuleException exception)
        {
            throw ToRpcException(exception);
        }
    }

    public override async Task<GameReply> JoinGame(
        JoinGameRequest request,
        ServerCallContext context)
    {
        try
        {
            // llamar JoinGameAsync
                 
            //--------

            return new GameReply
            {
                State = ToContract(result.State),
                PlayerId = result.PlayerId.ToString("D"),

                EventDeliveryWarning = result.EventDeliveryWarning ?? string.Empty
            };
        }
        catch (GameRuleException exception)
        {
            // Lnazar rpcException con ToRpcException

            //--------
        }
    }

    public override Task<GameReply> GetState(GetStateRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.GameId, out var gameId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "El identificador de partida no es válido."));
        }

        try
        {
            return Task.FromResult(new GameReply
            {
                State = ToContract(_coordinator.GetState(gameId))
            });
        }
        catch (GameRuleException exception)
        {
            throw ToRpcException(exception);
        }
    }

    public override async Task<GameReply> Roll(RollRequest request, ServerCallContext context)
    {
        /*
        1. Convierte el identificador de partida de la solicitud a GUID. Si no es válido, devuelve un error gRPC InvalidArgument con el mensaje «El identificador de partida no es válido.».
        2. Convierte el identificador de jugador de la solicitud a GUID. Si no es válido, devuelve un error gRPC InvalidArgument con el mensaje «El identificador de jugador no es válido.».
        3. Solicita al coordinador que realice la tirada de forma asíncrona, pasando ambos identificadores y el token de cancelación de la llamada.
        4. Construye la respuesta con el estado convertido al contrato, el identificador del jugador en formato GUID D y los valores de ambos dados. Incluye la advertencia de entrega de eventos; si no existe, usa una cadena vacía.
        5. Si el coordinador lanza una excepción de reglas del juego, conviértela en una excepción gRPC mediante ToRpcException y propágala.
        */
    }

    private static Game.Contracts.GameState ToContract(GameSnapshot state)
    {
        var result = new Game.Contracts.GameState
        {
            GameId = state.Id.ToString("D"),
            Name = state.Name,
            Capacity = state.Capacity,
            Status = state.Status switch
            {
                CoreGameStatus.WaitingForPlayers => ProtoGameStatus.WaitingForPlayers,
                CoreGameStatus.InProgress => ProtoGameStatus.InProgress,
                CoreGameStatus.Finished => ProtoGameStatus.Finished,
                _ => throw new InvalidOperationException($"Estado de partida no soportado: {state.Status}.")
            },
            CurrentPlayerId = state.CurrentPlayerId?.ToString("D") ?? string.Empty,
            WinnerId = state.WinnerId?.ToString("D") ?? string.Empty
        };

        result.Players.AddRange(state.Players.Select(player => new Game.Contracts.Player
        {
            PlayerId = player.Id.ToString("D"),
            Number = player.Number,
            Score = player.Score
        }));

        return result;
    }

    private static RpcException ToRpcException(GameRuleException exception)
    {
        var status = exception.Error switch
        {
            GameError.InvalidName or GameError.InvalidPlayerCount => StatusCode.InvalidArgument,
            GameError.GameNotFound => StatusCode.NotFound,
            GameError.GameAlreadyActive => StatusCode.AlreadyExists,
            GameError.GameFull or
                GameError.GameNotStarted or
                GameError.GameFinished or
                GameError.NotPlayersTurn => StatusCode.FailedPrecondition,
            _ => throw new InvalidOperationException($"Error de juego no soportado: {exception.Error}.")
        };

        return new RpcException(new Status(status, exception.Message));
    }
}
