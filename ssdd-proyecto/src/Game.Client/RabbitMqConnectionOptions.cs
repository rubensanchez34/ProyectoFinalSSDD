using Microsoft.Extensions.Configuration;

namespace Game.Client;

public sealed record RabbitMqConnectionOptions(string Host, string UserName, string Password, int Port = 5672)
{
    public static RabbitMqConnectionOptions FromConfiguration(IConfiguration configuration) =>
        new(
            ReadRequired(configuration, "Host", "RABBITMQ_HOST"),
            ReadRequired(configuration, "UserName", "RABBITMQ_USERNAME"),
            ReadRequired(configuration, "Password", "RABBITMQ_PASSWORD"),
            ReadPort(configuration));

    private static int ReadPort(IConfiguration configuration)
    {
        var value = configuration["RABBITMQ_PORT"];
        if (string.IsNullOrWhiteSpace(value))
        {
            value = configuration["RabbitMQ:Port"];
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return 5672;
        }

        if (!int.TryParse(value, out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                "La opción RabbitMQ:Port debe ser un entero entre 1 y 65535. Configúrala en appsettings.json o mediante RABBITMQ_PORT.");
        }

        return port;
    }

    private static string ReadRequired(IConfiguration configuration, string option, string environmentVariable)
    {
        var value = configuration[environmentVariable];
        if (string.IsNullOrWhiteSpace(value))
        {
            value = configuration[$"RabbitMQ:{option}"];
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"La opción RabbitMQ:{option} es obligatoria. Configúrala en appsettings.json o mediante {environmentVariable}.");
        }

        return value;
    }
}
