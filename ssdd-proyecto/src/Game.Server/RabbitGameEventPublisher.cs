using System.IO;
using System.Net.Sockets;
using Game.Core;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Game.Server;

public sealed class RabbitGameEventPublisher(RabbitMqConnectionOptions options)
    : IGameEventPublisher, IAsyncDisposable
{
    public const string ExchangeName = "distributed-dice";
    public const string TurnRoutingKey = "game.turn";
    public const string ScoresRoutingKey = "game.scores";
    public RabbitMqConnectionOptions ConnectionOptions { get; } = options;

    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private IConnection? _connection;
    private IChannel? _channel;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _ = await GetChannelAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsBrokerFailure(exception))
        {
            throw new GameEventDeliveryException("No se pudo conectar al servicio RabbitMQ.", exception);
        }
    }

    public async ValueTask PublishAsync(GameEvent gameEvent, CancellationToken cancellationToken)
    {
        var message = RabbitGameEventSerializer.Serialize(gameEvent);
        try
        {
            var channel = await GetChannelAsync(cancellationToken);
            // Publicar el mensaje en el exchange con la routing key y el body del mensaje.

            //--------
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsBrokerFailure(exception))
        {
            throw new GameEventDeliveryException(
                $"No se pudo publicar el evento {message.RoutingKey} en RabbitMQ.",
                exception);
        }
    }

    private async ValueTask<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        if (_connection is { IsOpen: true } && _channel is { IsOpen: true } openChannel)
        {
            return openChannel;
        }

        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true } && _channel is { IsOpen: true } existingChannel)
            {
                return existingChannel;
            }

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
            try{
            /*
            1. Crea la factoria de conexiones, 
            2. inicializa la conexión, 
            3. el canal
            4. y declara el exchange. 
            Si ocurre un error, se liberan los recursos y se lanza la excepción.
             */
            
                return _channel;
            }
            catch
            {
                if (_channel is not null)
                {
                    await _channel.DisposeAsync();
                    _channel = null;
                }

                await _connection.DisposeAsync();
                _connection = null;
                throw;
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private static bool IsBrokerFailure(Exception exception) =>
        exception is RabbitMQClientException or IOException or SocketException or TimeoutException;

    public async ValueTask DisposeAsync()
    {
        await _connectionGate.WaitAsync();
        try
        {
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
        finally
        {
            _connectionGate.Release();
            _connectionGate.Dispose();
        }
    }

}
