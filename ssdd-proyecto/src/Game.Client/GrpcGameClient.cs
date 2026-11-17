using Game.Contracts;
using Game.Core;
using Grpc.Core;
using Grpc.Net.Client;
using CoreGameStatus = Game.Core.GameStatus;
using ProtoGameStatus = Game.Contracts.GameStatus;

namespace Game.Client;

public sealed class GrpcGameClient : IPlayerGameClient, IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly GameService.GameServiceClient _client;

    public GrpcGameClient(string serverAddress, HttpMessageHandler? httpHandler = null)
    {
        // inicializa y cliente gRPC con la dirección del servidor y el manejador HTTP opcional.
    }

    public Task<CreateGameResult> CreateGameAsync(
        string name,
        int playerCount,
        CancellationToken cancellationToken) =>
        CallAsync(async () =>
        {
            /*
            1. Prepara la solicitud gRPC. Crea un objeto CreateGameRequest y asigna:
                A Name, el nombre de la partida recibido en el parámetro name.
                A PlayerCount, el número de jugadores recibido en playerCount.
            2. Llama al servidor. Usa _client.CreateGameAsync con la solicitud anterior y pasa cancellationToken como token de cancelación. Espera la respuesta con await y guárdala en una variable, por ejemplo reply.
            3. Convierte el estado recibido. La respuesta contiene el estado de la partida en reply.State, expresado con los tipos del contrato gRPC. Pásalo a ToSnapshot y guarda el resultado en state, que utiliza los modelos de la aplicación.

            4. Obtén el identificador del jugador. El servidor devuelve reply.PlayerId como texto. Pásalo a ParseRequiredGuid junto con "jugador" para obtener un GUID obligatorio y detectar un identificador inválido.

            5. Devuelve el resultado. Crea un CreateGameResult con estos tres datos, en orden:
                state.Id: identificador de la partida recién creada.
                ParseRequiredGuid(reply.PlayerId, "jugador"): El GUID del jugador obtenido en el paso anterior.
                state: la instantánea completa del estado inicial.
            */
        }, cancellationToken);

    public Task<JoinGameResult> JoinGameAsync(string name, CancellationToken cancellationToken) =>
        CallAsync(async () =>
        {
            /*
            1. Prepara la solicitud gRPC. Crea un objeto JoinGameRequest y asigna a su propiedad Name el nombre de la partida recibido en el parámetro name.
            2. Llama al servidor. Usa _client.JoinGameAsync con la solicitud anterior y pasa cancellationToken como token de cancelación. Espera la respuesta con await y guárdala en una variable, por ejemplo reply.
            3. Obtén el identificador del jugador. El servidor devuelve reply.PlayerId como texto. Pásalo a ParseRequiredGuid junto con "jugador" para obtener un GUID obligatorio y detectar un identificador inválido.
            4. Convierte el estado recibido. Pasa reply.State, expresado con los tipos del contrato gRPC, a ToSnapshot para obtener una instantánea con los modelos de la aplicación.
            5. Procesa la advertencia. Pasa reply.EventDeliveryWarning a EmptyToNull para convertir una advertencia vacía en null.
            6. Devuelve el resultado. Crea un JoinGameResult con estos tres datos, en orden:
                El GUID del jugador obtenido en el paso 3.
                La instantánea del estado obtenida en el paso 4.
                La advertencia procesada en el paso 5.
            */
        }, cancellationToken);

    public Task<GameSnapshot> GetStateAsync(Guid gameId, CancellationToken cancellationToken) =>
        CallAsync(async () =>
        {
            var reply = await _client.GetStateAsync(
                new GetStateRequest { GameId = gameId.ToString("D") },
                cancellationToken: cancellationToken);
            return ToSnapshot(reply.State);
        }, cancellationToken);

    public Task<RollResult> RollAsync(
        Guid gameId,
        Guid playerId,
        CancellationToken cancellationToken) =>
        CallAsync(async () =>
        {
            /*
            1. Prepara la solicitud gRPC. Crea un objeto RollRequest y asigna:
                A GameId, el identificador de partida gameId convertido a texto en formato GUID "D".
                A PlayerId, el identificador playerId convertido al mismo formato.
            2. Llama al servidor. Usa _client.RollAsync con la solicitud anterior y pasa cancellationToken como token de cancelación. Espera la respuesta con await y guárdala en una variable, por ejemplo reply.
            3. Obtén el identificador del jugador. Pasa reply.PlayerId a ParseRequiredGuid junto con "jugador" para convertir el texto recibido en un GUID obligatorio y detectar un identificador inválido.
            4. Recoge los dados. Obtén reply.FirstDie y reply.SecondDie, que contienen los valores de los dos dados devueltos por el servidor.
            5. Convierte el estado recibido. Pasa reply.State a ToSnapshot para obtener una instantánea con los modelos de la aplicación.
            6. Procesa la advertencia. Pasa reply.EventDeliveryWarning a EmptyToNull para convertir una advertencia vacía en null.
            7. Devuelve el resultado. Crea un RollResult con estos cinco datos, en orden:
                El GUID del jugador.
                El valor del primer dado.
                El valor del segundo dado.
                La instantánea del estado.
                La advertencia procesada.
            */
        }, cancellationToken);

    private static async Task<T> CallAsync<T>(
        Func<Task<T>> call,
        CancellationToken cancellationToken)
    {
        try
        {
            return await call();
        }
        catch (RpcException exception) when (
            exception.StatusCode != StatusCode.Cancelled || !cancellationToken.IsCancellationRequested)
        {
            var message = string.IsNullOrWhiteSpace(exception.Status.Detail)
                ? $"El servidor rechazó la operación ({exception.StatusCode})."
                : exception.Status.Detail;
            throw new GameClientException(exception.StatusCode, message, exception);
        }
    }

    private static GameSnapshot ToSnapshot(Game.Contracts.GameState? state)
    {
        if (state is null)
        {
            throw new InvalidDataException("El servidor devolvió una respuesta sin estado de partida.");
        }

        var players = state.Players
            .Select(player => new PlayerSnapshot(
                ParseRequiredGuid(player.PlayerId, "jugador"),
                player.Number,
                player.Score))
            .ToArray();

        return new GameSnapshot(
            ParseRequiredGuid(state.GameId, "partida"),
            state.Name,
            state.Capacity,
            state.Status switch
            {
                ProtoGameStatus.WaitingForPlayers => CoreGameStatus.WaitingForPlayers,
                ProtoGameStatus.InProgress => CoreGameStatus.InProgress,
                ProtoGameStatus.Finished => CoreGameStatus.Finished,
                _ => throw new InvalidDataException($"El servidor devolvió un estado de partida desconocido: {state.Status}.")
            },
            players,
            ParseOptionalGuid(state.CurrentPlayerId, "jugador del turno"),
            ParseOptionalGuid(state.WinnerId, "ganador"));
    }

    private static Guid ParseRequiredGuid(string value, string valueName) =>
        Guid.TryParse(value, out var result)
            ? result
            : throw new InvalidDataException($"El servidor devolvió un identificador de {valueName} inválido.");

    private static Guid? ParseOptionalGuid(string value, string valueName) =>
        string.IsNullOrEmpty(value) ? null : ParseRequiredGuid(value, valueName);

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();
        return ValueTask.CompletedTask;
    }
}
