using System.Text;
using Game.Core;
using Google.Protobuf;

namespace Game.Client;

public static class RabbitGameEventDeserializer
{
    public static GameEvent Deserialize(string routingKey, ReadOnlyMemory<byte> body)
    {
        var json = Encoding.UTF8.GetString(body.Span);
        return routingKey switch
        {
            RabbitGameEventSubscriber.TurnRoutingKey => DeserializeTurn(
                JsonParser.Default.Parse<Game.Contracts.TurnEvent>(json)),
            RabbitGameEventSubscriber.ScoresRoutingKey => DeserializeScores(
                JsonParser.Default.Parse<Game.Contracts.ScoresEvent>(json)),
            _ => throw new InvalidDataException($"Routing key RabbitMQ no soportada: {routingKey}.")
        };
    }

    private static TurnChangedEvent DeserializeTurn(Game.Contracts.TurnEvent message) =>
        new(
            ParseGameId(message.GameId),
            message.CurrentPlayerNumber == 0 ? null : message.CurrentPlayerNumber,
            message.WinnerPlayerNumber == 0 ? null : message.WinnerPlayerNumber);

    private static ScoresUpdatedEvent DeserializeScores(Game.Contracts.ScoresEvent message)
    {
        var scores = message.Scores
            .Select(score => new PlayerScore(
                ParseGameId(score.PlayerId),
                score.PlayerNumber,
                score.Score))
            .ToArray();
        Guid? winnerId = null;
        if (message.WinnerPlayerNumber != 0)
        {
            winnerId = scores
                .FirstOrDefault(score => score.PlayerNumber == message.WinnerPlayerNumber)?.PlayerId
                ?? throw new InvalidDataException("El evento indica un ganador ausente de las puntuaciones.");
        }

        return new ScoresUpdatedEvent(ParseGameId(message.GameId), scores, winnerId);
    }

    private static Guid ParseGameId(string value) =>
        Guid.TryParse(value, out var gameId)
            ? gameId
            : throw new InvalidDataException("El evento RabbitMQ contiene un identificador inválido.");
}
