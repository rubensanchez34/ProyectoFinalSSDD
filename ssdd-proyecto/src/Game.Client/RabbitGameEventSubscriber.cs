using System.IO;
using System.Net.Sockets;
using System.Threading.Channels;
using Game.Core;
using Google.Protobuf;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;

namespace Game.Client;

public sealed class RabbitGameEventSubscriber(RabbitMqConnectionOptions options)
    : IPlayerEventSubscriber, IAsyncDisposable
{
    public RabbitMqConnectionOptions ConnectionOptions { get; } = options;

    public const string ExchangeName = "distributed-dice";
    public const string TurnRoutingKey = "game.turn";
    public const string ScoresRoutingKey = "game.scores";

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;
    private Channel<GameEvent>? _events;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true } && _channel is { IsOpen: true } && _events is not null)
            {
                return;
            }

            await DisposeResourcesAsync();
            var events = Channel.CreateUnbounded<GameEvent>(
                new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

            try
            {
                /*
                1. Crea la factoría de conexiones.
                2. Inicializa la conexión con la factoría.
                */
                IChannel? channel = null;
                try
                {
                    // 3. Inicializa el canal con la conexión.
                    // 4. Declara el exchange con el nombre ExchangeName, tipo "topic" y durable.




                    //--------
                    
                    /*
                    5. Declara una cola temporal y exclusiva para recibir los eventos.
                    6. Vincula la cola al exchange con la routing key TurnRoutingKey.
                    7. Vincula la cola al exchange con la routing key ScoresRoutingKey.
                    (o crea dos colas separadas, una para cada routing key, y vincula cada una al exchange con su routing key correspondiente)
                    */



                    //--------
                
                    Task HandleEventReceivedAsync(object sender, BasicDeliverEventArgs delivery)
                    {
                        try
                        {
                            events.Writer.TryWrite(
                                RabbitGameEventDeserializer.Deserialize(delivery.RoutingKey, delivery.Body));
                        }
                        catch (Exception exception) when (
                            exception is InvalidDataException or InvalidProtocolBufferException or FormatException)
                        {
                            events.Writer.TryComplete(
                                new GameEventSubscriptionException("Se recibió un evento RabbitMQ inválido.", exception));
                        }

                        return Task.CompletedTask;
                    }

                    Task HandleConsumerShutdownAsync(object sender, ShutdownEventArgs shutdown)
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            events.Writer.TryComplete(
                                new GameEventSubscriptionException(
                                    "El canal de eventos RabbitMQ se cerró.",
                                    new IOException("Canal RabbitMQ cerrado.")));
                        }

                        return Task.CompletedTask;
                    }

                    // 8. Crea un consumidor asíncrono y suscríbete a los eventos Received y Shutdown con los manejadores anteriores.
                    

                    //--------

                    // 9. Inicia la suscripción a la cola con el consumidor creado, usando autoAck = true.
                    
                    //--------

                    // 10. Asigna la conexión (connection), el canal (channel) y el canal de eventos (events) a los campos de la clase (_connection, _channel, _events).

                    //--------
                }
                catch
                {
                    if (channel is not null)
                    {
                        // dispose del canal
                    }

                    //dispose de la conexión

                    //--------

                    throw;
                }
            }
            catch (Exception exception) when (IsBrokerFailure(exception))
            {
                events.Writer.TryComplete();
                throw new GameEventSubscriptionException(
                    "No se pudo iniciar la suscripción a RabbitMQ.",
                    exception);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public IAsyncEnumerable<GameEvent> ReadEventsAsync(CancellationToken cancellationToken)
    {
        var events = _events ??
            throw new InvalidOperationException("La suscripción RabbitMQ debe iniciarse antes de leer eventos.");
        return events.Reader.ReadAllAsync(cancellationToken);
    }

    private static bool IsBrokerFailure(Exception exception) =>
        exception is RabbitMQClientException or IOException or SocketException or TimeoutException;

    private async ValueTask DisposeResourcesAsync()
    {
        _events?.Writer.TryComplete();
        _events = null;
        if (_channel is not null)
        {
            await _channel.DisposeAsync();
            _channel = null;
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await DisposeResourcesAsync();
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }
}
