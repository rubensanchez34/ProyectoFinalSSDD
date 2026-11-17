using System.Text;
using Game.Core;
using Google.Protobuf;

namespace Game.Server;

public static class RabbitGameEventSerializer
{
    public static (string RoutingKey, byte[] Body) Serialize(GameEvent gameEvent)
    {
        var (routingKey, json) = gameEvent switch
        {
            TurnChangedEvent turn =>
                (RabbitGameEventPublisher.TurnRoutingKey, JsonFormatter.Default.Format(new Game.Contracts.TurnEvent
                {
                    GameId = turn.GameId.ToString("D"),
                    CurrentPlayerNumber = turn.CurrentPlayerNumber ?? 0,
                    WinnerPlayerNumber = turn.WinnerPlayerNumber ?? 0
                })),
            ScoresUpdatedEvent scores =>
                (RabbitGameEventPublisher.ScoresRoutingKey, JsonFormatter.Default.Format(new Game.Contracts.ScoresEvent
                {
                    GameId = scores.GameId.ToString("D"),
                    Scores =
                    {
                        scores.Scores.Select(score => new Game.Contracts.PlayerScore
                        {
                            PlayerId = score.PlayerId.ToString("D"),
                            PlayerNumber = score.PlayerNumber,
                            Score = score.Score
                        })
                    },
                    WinnerPlayerNumber = scores.WinnerId is Guid winnerId
                        ? scores.Scores.FirstOrDefault(score => score.PlayerId == winnerId)?.PlayerNumber ?? 0
                        : 0
                })),
            _ => throw new ArgumentOutOfRangeException(nameof(gameEvent), gameEvent, "Tipo de evento no soportado.")
        };

        return (routingKey, Encoding.UTF8.GetBytes(json));
    }
}
