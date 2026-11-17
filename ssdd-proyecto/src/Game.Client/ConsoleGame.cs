using Game.Core;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Text;

namespace Game.Client;

public sealed class ConsoleGame(
    IPlayerGameClient gameClient,
    IPlayerEventSubscriber eventSubscriber,
    IAnsiConsole ansiConsole,
    IAnsiConsoleInput? consoleInput = null)
{
    private readonly IAnsiConsole _ansiConsole = ansiConsole;
    private readonly IAnsiConsoleInput _consoleInput = consoleInput ?? ansiConsole.Input;
    private Guid? _gameId;
    private Guid? _playerId;
    private GameSnapshot? _currentState;
    private (int FirstDie, int SecondDie)? _lastRoll;
    // BEGIN SOLUTION
    private bool _eventStreamAvailable = true;
    private List<GameEvent>? _eventsPendingGameAssociation;
    private const int MaximumLiveMessages = 12;
    private const int WinningScore = 20;

    public async Task RunInteractiveAsync(CancellationToken cancellationToken = default)
    {
        var messages = new List<string> { "Comandos: crear <nombre> <jugadores>, unir <nombre>, estado, lanzar, salir" };
        var commandBuffer = new StringBuilder();
        using var eventCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IAsyncEnumerator<GameEvent>? eventEnumerator = null;
        Task<bool>? eventRead = null;
        try
        {
            try
            {
                await eventSubscriber.StartAsync(cancellationToken);
            }
            catch (GameEventSubscriptionException exception)
            {
                _eventStreamAvailable = false;
                AppendLiveMessages(messages, $"Error de eventos RabbitMQ: {exception.Message}{Environment.NewLine}El estado se reconciliará por gRPC antes de la siguiente tirada.");
            }

            if (_eventStreamAvailable)
            {
                eventEnumerator = eventSubscriber
                    .ReadEventsAsync(eventCancellation.Token)
                    .GetAsyncEnumerator(eventCancellation.Token);
                eventRead = eventEnumerator.MoveNextAsync().AsTask();
            }

            await _ansiConsole
                .Live(CreateLiveView(messages, commandBuffer.ToString()))
                .AutoClear(false)
                .StartAsync(async context =>
                {
                    void RefreshView()
                    {
                        context.UpdateTarget(CreateLiveView(messages, commandBuffer.ToString()));
                        context.Refresh();
                    }

                    RefreshView();
                    var inputRead = _consoleInput.ReadKeyAsync(
                        intercept: true,
                        cancellationToken: cancellationToken);
                    while (true)
                    {
                        var completed = eventRead is null
                            ? inputRead
                            : await Task.WhenAny(inputRead, eventRead);
                        if (completed == inputRead && eventRead is { IsCompleted: true })
                        {
                            completed = eventRead;
                        }

                        if (completed == eventRead)
                        {
                            try
                            {
                                if (!await eventRead!)
                                {
                                    eventRead = null;
                                    _eventStreamAvailable = false;
                                    AppendLiveMessages(messages, "La conexión de eventos se cerró; se consultará el estado por gRPC antes de lanzar.");
                                    RefreshView();
                                    continue;
                                }

                                var currentEventEnumerator = eventEnumerator ??
                                    throw new InvalidOperationException("El flujo de eventos no está inicializado.");
                                var gameEvent = currentEventEnumerator.Current;
                                if (_eventsPendingGameAssociation is { } pendingEvents)
                                {
                                    pendingEvents.Add(gameEvent);
                                }
                                else if (_gameId is Guid activeGameId && gameEvent.GameId == activeGameId)
                                {
                                    ApplyGameEvent(gameEvent);
                                    using var eventOutput = new StringWriter();
                                    await WriteEventAsync(gameEvent, eventOutput);
                                    AppendLiveMessages(messages, eventOutput.ToString());
                                    RefreshView();
                                }

                                eventRead = currentEventEnumerator.MoveNextAsync().AsTask();
                            }
                            catch (GameEventSubscriptionException exception)
                            {
                                eventRead = null;
                                _eventStreamAvailable = false;
                                AppendLiveMessages(messages, $"Error de eventos RabbitMQ: {exception.Message}{Environment.NewLine}El estado se reconciliará por gRPC antes de la siguiente tirada.");
                                RefreshView();
                            }

                            continue;
                        }

                        var key = await inputRead;
                        if (key is null)
                        {
                            return;
                        }

                        var consoleKey = key.Value;
                        if (consoleKey.Key == ConsoleKey.C &&
                            consoleKey.Modifiers.HasFlag(ConsoleModifiers.Control))
                        {
                            return;
                        }

                        if (consoleKey.Key == ConsoleKey.Enter)
                        {
                            var line = commandBuffer.ToString();
                            commandBuffer.Clear();
                            using var commandOutput = new StringWriter();
                            var shouldExit = await HandleCommandAsync(
                                line,
                                commandOutput,
                                cancellationToken);
                            AppendLiveMessages(messages, commandOutput.ToString());
                            RefreshView();
                            if (shouldExit)
                            {
                                return;
                            }
                        }
                        else if (consoleKey.Key == ConsoleKey.Backspace)
                        {
                            if (commandBuffer.Length > 0)
                            {
                                commandBuffer.Length--;
                            }

                            RefreshView();
                        }
                        else if (!char.IsControl(consoleKey.KeyChar))
                        {
                            commandBuffer.Append(consoleKey.KeyChar);
                            RefreshView();
                        }

                        inputRead = _consoleInput.ReadKeyAsync(
                            intercept: true,
                            cancellationToken: cancellationToken);
                    }
                });
        }
        finally
        {
            await CancelEventsAsync(eventCancellation, eventRead);
            if (eventEnumerator is not null)
            {
                await eventEnumerator.DisposeAsync();
            }
        }
    }

    private IRenderable CreateLiveView(IReadOnlyCollection<string> messages, string commandBuffer)
    {
        var state = _currentState;
        var currentPlayer = state is null
            ? null
            : state.Players.FirstOrDefault(player => player.Id == state.CurrentPlayerId);
        var lastRollText = _lastRoll is { } roll
            ? $"ÚLTIMA TIRADA: {roll.FirstDie} + {roll.SecondDie} = {roll.FirstDie + roll.SecondDie}"
            : "ESPERANDO LA PRIMERA TIRADA";
        var commandMarkup = Markup.Escape(commandBuffer);
        var gameName = state is null ? "SIN PARTIDA" : Markup.Escape(state.Name);

        var banner = new Rows(
            new Align(
                new Markup("[red]========[/][orange1]========[/][yellow]========[/][green]========[/][cyan]========[/]"),
                HorizontalAlignment.Center),
            new Align(new FigletText("DADOS SSDD").Color(Color.Orange1), HorizontalAlignment.Center),
            new Align(
                new Markup("[red]========[/][orange1]========[/][yellow]========[/][green]========[/][cyan]========[/]"),
                HorizontalAlignment.Center));

        var eventLog = messages
            .TakeLast(4)
            .Select(CreateLiveEventMessage)
            .ToArray();
        IRenderable eventContent = eventLog.Length == 0
            ? new Text("Sin eventos recientes.")
            : new Rows(eventLog);

        var isPlayersTurn = state is { Status: GameStatus.InProgress } activeState &&
            activeState.CurrentPlayerId == _playerId;
        var menuMarkup = string.Join(
            Environment.NewLine,
            isPlayersTurn ? "[bold yellow]> LANZAR DADOS[/]" : "[dim]  LANZAR DADOS[/]",
            "  NUEVA PARTIDA  (crear <nombre> <jugadores>)",
            "  UNIRSE         (unir <nombre>)",
            "  CONSULTAR      (estado)",
            "  SALIR",
            "[dim]Escribe el comando y pulsa Enter.[/]");

        var panel = new Panel(
                new Rows(
                    banner,
                    new Align(
                        new Markup($"[dim]PARTIDA: {gameName}  |  META: {WinningScore} PUNTOS[/]"),
                        HorizontalAlignment.Center),
                    new Align(new Markup("[yellow]* MARCADOR *[/]"), HorizontalAlignment.Center),
                    CreateScoreTable(state),
                    new Align(
                        new Markup(BuildTurnStatus(state, currentPlayer)),
                        HorizontalAlignment.Center),
                    new Align(
                        new Columns(
                            CreateDiePanel(1, _lastRoll?.FirstDie),
                            CreateDiePanel(2, _lastRoll?.SecondDie))
                        {
                            Expand = false
                        },
                        HorizontalAlignment.Center),
                    new Align(
                        new Markup($"[bold cyan]{lastRollText}[/]"),
                        HorizontalAlignment.Center),
                    new Panel(eventContent)
                        .Header("[yellow]EVENTOS RECIENTES[/]", Justify.Center)
                        .BorderColor(Color.Grey)
                        .Expand(),
                    new Markup(menuMarkup),
                    new Markup($"[bold green]>[/] [yellow]Comando: {commandMarkup}[/]")))
            .Header("[bold yellow] DADOS DISTRIBUIDOS // SSDD [/]", Justify.Center)
            .Border(BoxBorder.Heavy)
            .BorderColor(Color.Green)
            .Padding(1, 0);
        panel.Width = Math.Min(90, Math.Max(1, _ansiConsole.Profile.Width - 2));
        return new Align(panel, HorizontalAlignment.Center);
    }

    private static void AppendLiveMessages(List<string> messages, string output)
    {
        messages.AddRange(output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        if (messages.Count > MaximumLiveMessages)
        {
            messages.RemoveRange(0, messages.Count - MaximumLiveMessages);
        }
    }

    private static Table CreateScoreTable(GameSnapshot? state)
    {
        var table = new Table()
            .Border(TableBorder.HeavyEdge)
            .BorderColor(Color.Green)
            .Expand();
        table.AddColumn(new TableColumn("[yellow] [/]").Centered());
        table.AddColumn("[yellow]JUGADOR[/]");
        table.AddColumn(new TableColumn("[yellow]PUNTOS[/]").RightAligned());
        table.AddColumn("[yellow]PROGRESO[/]");

        if (state is null || state.Players.Count == 0)
        {
            table.AddRow(
                new Text(" "),
                new Text("ESPERANDO JUGADORES"),
                new Text("--"),
                new Text(new string('░', 10)));
            return table;
        }

        foreach (var player in state.Players.OrderBy(player => player.Number))
        {
            var color = GetPlayerColor(player.Number);
            var progress = Math.Clamp(player.Score, 0, WinningScore) * 10 / WinningScore;
            var isCurrentPlayer = state.Status == GameStatus.InProgress &&
                player.Id == state.CurrentPlayerId;
            IRenderable indicator = state.WinnerId == player.Id
                ? new Markup("[yellow]👑[/]")
                : isCurrentPlayer
                    ? new Markup("[green]>[/]")
                    : new Text(" ");
            table.AddRow(
                indicator,
                new Text($"JUGADOR {player.Number}", new Style(color)),
                new Text(player.Score.ToString("000"), new Style(color)),
                new Markup(
                    $"[{GetPlayerColorTag(player.Number)}]{new string('█', progress)}[/][grey]{new string('░', 10 - progress)}[/]"));
        }

        return table;
    }

    private static Panel CreateDiePanel(int number, int? value)
    {
        var face = new Table().NoBorder().HideHeaders();
        for (var column = 0; column < 3; column++)
        {
            var faceColumn = new TableColumn(" ")
            {
                Padding = new Padding(0),
                Width = 3
            };
            face.AddColumn(faceColumn.Centered());
        }

        for (var row = 0; row < 3; row++)
        {
            var cells = new string[3];
            for (var column = 0; column < 3; column++)
            {
                cells[column] = HasDiePip(value, row, column)
                    ? "[red]●[/]"
                    : value is null && row == 1 && column == 1
                        ? "[dim]?[/]"
                        : " ";
            }

            face.AddRow(cells);
        }

        return new Panel(face)
            .Header($"[dim]DADO {number}[/]", Justify.Center)
            .Border(BoxBorder.Square)
            .BorderColor(Color.Grey)
            .Padding(0, 0);
    }

    private static bool HasDiePip(int? value, int row, int column) =>
        value switch
        {
            1 => row == 1 && column == 1,
            2 => row == column,
            3 => row == column || row == 1 && column == 1,
            4 => row != 1 && column != 1,
            5 => row != 1 && column != 1 || row == 1 && column == 1,
            6 => column != 1,
            _ => false
        };

    private static string BuildTurnStatus(GameSnapshot? state, PlayerSnapshot? currentPlayer)
    {
        if (state is null)
        {
            return "[yellow]CREA O ÚNETE A UNA PARTIDA[/]";
        }

        return state.Status switch
        {
            GameStatus.WaitingForPlayers =>
                $"[yellow]ESPERANDO JUGADORES ({state.Players.Count}/{state.Capacity})[/]",
            GameStatus.InProgress when currentPlayer is not null =>
                $"[bold green]TURNO DE JUGADOR {currentPlayer.Number}[/]",
            GameStatus.Finished when state.WinnerId is Guid winnerId =>
                $"[bold red]PARTIDA FINALIZADA · GANADOR: JUGADOR {state.Players.FirstOrDefault(player => player.Id == winnerId)?.Number}[/]",
            GameStatus.Finished => "[bold red]PARTIDA FINALIZADA[/]",
            _ => "[dim]ESPERANDO EL INICIO DE LA PARTIDA[/]"
        };
    }

    private static IRenderable CreateLiveEventMessage(string message) =>
        message.StartsWith("Partida finalizada. Ganador:", StringComparison.Ordinal)
            ? new Markup($"[red]{Markup.Escape(message)}[/]")
            : new Text(message);

    private static Color GetPlayerColor(int playerNumber) =>
        playerNumber switch
        {
            1 => Color.Red,
            2 => Color.Green,
            3 => Color.Blue,
            _ => Color.Magenta
        };

    private static string GetPlayerColorTag(int playerNumber) =>
        playerNumber switch
        {
            1 => "red",
            2 => "green",
            3 => "blue",
            _ => "magenta"
        };

    private void ApplyGameEvent(GameEvent gameEvent)
    {
        if (_currentState is not { } state || state.Id != gameEvent.GameId)
        {
            return;
        }

        switch (gameEvent)
        {
            case ScoresUpdatedEvent scores:
                var updatedPlayers = scores.Scores
                    .Select(score => new PlayerSnapshot(score.PlayerId, score.PlayerNumber, score.Score))
                    .OrderBy(player => player.Number)
                    .ToArray();
                _currentState = state with
                {
                    Players = updatedPlayers,
                    WinnerId = scores.WinnerId,
                    Status = scores.WinnerId is null ? state.Status : GameStatus.Finished
                };
                break;
            case TurnChangedEvent turn:
                var currentPlayer = turn.CurrentPlayerNumber is int currentNumber
                    ? state.Players.FirstOrDefault(player => player.Number == currentNumber)
                    : null;
                var winner = turn.WinnerPlayerNumber is int winnerNumber
                    ? state.Players.FirstOrDefault(player => player.Number == winnerNumber)
                    : null;
                _currentState = state with
                {
                    CurrentPlayerId = currentPlayer?.Id,
                    WinnerId = winner?.Id,
                    Status = winner is not null
                        ? GameStatus.Finished
                        : currentPlayer is not null
                            ? GameStatus.InProgress
                            : state.Status
                };
                break;
        }
    }

    public async Task RunAsync(
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        await output.WriteLineAsync("Comandos: crear <nombre> <jugadores>, unir <nombre>, estado, lanzar, salir");
        using var eventCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        IAsyncEnumerator<GameEvent>? eventEnumerator = null;
        Task<bool>? eventRead = null;
        try
        {
            try
            {
                await eventSubscriber.StartAsync(cancellationToken);
            }
            catch (GameEventSubscriptionException exception)
            {
                _eventStreamAvailable = false;
                await output.WriteLineAsync($"Error de eventos RabbitMQ: {exception.Message}");
                await output.WriteLineAsync("El estado se reconciliará por gRPC antes de la siguiente tirada.");
            }

            if (_eventStreamAvailable)
            {
                eventEnumerator = eventSubscriber
                    .ReadEventsAsync(eventCancellation.Token)
                    .GetAsyncEnumerator(eventCancellation.Token);
                eventRead = eventEnumerator.MoveNextAsync().AsTask();
            }

            var inputRead = input.ReadLineAsync(cancellationToken).AsTask();
            while (true)
            {
                if (eventRead is null)
                {
                    var line = await inputRead;
                    if (line is null || await HandleCommandAsync(line, output, cancellationToken))
                    {
                        return;
                    }

                    inputRead = input.ReadLineAsync(cancellationToken).AsTask();
                    continue;
                }

                var completed = await Task.WhenAny(inputRead, eventRead);
                if (completed == inputRead && eventRead.IsCompleted)
                {
                    completed = eventRead;
                }

                if (completed == eventRead)
                {
                    try
                    {
                        if (!await eventRead)
                        {
                            eventRead = null;
                            _eventStreamAvailable = false;
                            await output.WriteLineAsync("La conexión de eventos se cerró; se consultará el estado por gRPC antes de lanzar.");
                            continue;
                        }

                        var currentEventEnumerator = eventEnumerator ??
                            throw new InvalidOperationException("El flujo de eventos no está inicializado.");
                        var gameEvent = currentEventEnumerator.Current;
                        if (_eventsPendingGameAssociation is { } pendingEvents)
                        {
                            pendingEvents.Add(gameEvent);
                        }
                        else if (_gameId is Guid activeGameId && gameEvent.GameId == activeGameId)
                        {
                            ApplyGameEvent(gameEvent);
                            await WriteEventAsync(gameEvent, output);
                        }

                        eventRead = currentEventEnumerator.MoveNextAsync().AsTask();
                    }
                    catch (GameEventSubscriptionException exception)
                    {
                        eventRead = null;
                        _eventStreamAvailable = false;
                        await output.WriteLineAsync($"Error de eventos RabbitMQ: {exception.Message}");
                        await output.WriteLineAsync("El estado se reconciliará por gRPC antes de la siguiente tirada.");
                    }

                    continue;
                }

                var inputLine = await inputRead;
                if (inputLine is null || await HandleCommandAsync(
                        inputLine,
                        output,
                        cancellationToken))
                {
                    return;
                }

                inputRead = input.ReadLineAsync(cancellationToken).AsTask();
            }
        }
        finally
        {
            await CancelEventsAsync(eventCancellation, eventRead);
            if (eventEnumerator is not null)
            {
                await eventEnumerator.DisposeAsync();
            }
        }
    }

    private static async Task CancelEventsAsync(
        CancellationTokenSource cancellation,
        Task<bool>? pendingRead)
    {
        cancellation.Cancel();
        if (pendingRead is null)
        {
            return;
        }

        try
        {
            await pendingRead;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
    }

    private async Task<bool> HandleCommandAsync(
        string line,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var separator = line.IndexOf(' ');
        var command = (separator < 0 ? line : line[..separator]).Trim().ToLowerInvariant();
        var arguments = separator < 0 ? string.Empty : line[(separator + 1)..].Trim();

        try
        {
            switch (command)
            {
                case "salir":
                case "exit":
                    return true;
                case "crear":
                {
                    var lastSeparator = arguments.LastIndexOf(' ');
                    if (lastSeparator < 1 ||
                        !int.TryParse(arguments[(lastSeparator + 1)..], out var playerCount))
                    {
                        await output.WriteLineAsync("Uso: crear <nombre> <jugadores>");
                        break;
                    }

                    var result = await gameClient.CreateGameAsync(
                        arguments[..lastSeparator].Trim(),
                        playerCount,
                        cancellationToken);

                        // asignar a _gameId, _playerId y _currentState con los valores de result
                    _gameId = ;
                    _playerId = ;
                    _currentState = ;
                    _lastRoll = null;
                    await output.WriteLineAsync($"Partida creada. Id: {result.GameId}");
                    break;
                }
                case "unir":
                {
                    if (string.IsNullOrWhiteSpace(arguments))
                    {
                        await output.WriteLineAsync("Uso: unir <nombre>");
                        break;
                    }

                    var pendingEvents = new List<GameEvent>();
                    _eventsPendingGameAssociation = pendingEvents;
                    JoinGameResult result;
                    try
                    {
                        // llamar JoinGameAsync(arguments, cancellationToken) y asignar a result

                        //--------
                    }
                    finally
                    {
                        _eventsPendingGameAssociation = null;
                    }
    
                    // asignar a _gameId, _playerId y _currentState con los valores de result
                    _gameId = ;
                    _playerId = ;
                    _currentState = ;
                    _lastRoll = null;
                    await output.WriteLineAsync($"Unido a la partida {result.State.Name}.");
                    foreach (var gameEvent in pendingEvents.Where(gameEvent => gameEvent.GameId == _gameId))
                    {
                        ApplyGameEvent(gameEvent);
                        await WriteEventAsync(gameEvent, output);
                    }

                    await WriteWarningAsync(result.EventDeliveryWarning, output);
                    break;
                }
                case "estado":
                    if (_gameId is not Guid statusGameId)
                    {
                        await output.WriteLineAsync("Primero crea o únete a una partida.");
                        break;
                    }

                    _currentState = await gameClient.GetStateAsync(statusGameId, cancellationToken);
                    await WriteSnapshotAsync(_currentState, output);
                    break;
                case "lanzar":
                    /*
                    1. Comprueba que el jugador está en una partida. Verifica que _gameId y _playerId contienen identificadores GUID. Si alguno falta, escribe «Primero crea o únete a una partida.» y sal del caso actual con break. Si ambos son válidos, guárdalos como rollGameId y rollPlayerId.
                    2. Consulta el estado si no llegan eventos. Si _eventStreamAvailable es falso, llama de forma asíncrona a gameClient.GetStateAsync con rollGameId y cancellationToken. Muestra el estado obtenido mediante WriteSnapshotAsync, pasando también output.
                    3. Solicita la tirada. Llama a gameClient.RollAsync con los identificadores de partida y jugador y el token de cancelación. Espera el resultado y guárdalo en roll.
                    4. Actualiza los datos locales. Guarda roll.State en _currentState y los valores roll.FirstDie y roll.SecondDie como pareja en _lastRoll.
                    5. Muestra el resultado. Escribe en output los valores de ambos dados y el mensaje «Puntuación actualizada.».
                    6. Muestra la advertencia si corresponde. Llama a WriteWarningAsync con roll.EventDeliveryWarning y output, y espera a que termine.
                    */
                    break;
                case "":
                    break;
                default:
                    await output.WriteLineAsync("Comando no reconocido.");
                    break;
            }
        }
        catch (GameClientException exception)
        {
            await output.WriteLineAsync($"Error gRPC ({exception.StatusCode}): {exception.Message}");
        }

        return false;
    }

    private static Task WriteWarningAsync(string? warning, TextWriter output) =>
        string.IsNullOrWhiteSpace(warning)
            ? Task.CompletedTask
            : output.WriteLineAsync($"Advertencia: {warning}");

    private async Task WriteEventAsync(GameEvent gameEvent, TextWriter output)
    {
        switch (gameEvent)
        {
            case TurnChangedEvent turn when turn.CurrentPlayerNumber is int playerNumber:
                await output.WriteLineAsync($"Turno: jugador {playerNumber}.");
                break;
            case TurnChangedEvent turn:
                await output.WriteLineAsync($"Partida finalizada. Ganador: jugador {turn.WinnerPlayerNumber}.");
                break;
            case ScoresUpdatedEvent scores:
                var formattedScores = string.Join(
                    ", ",
                    scores.Scores.Select(score => $"jugador {score.PlayerNumber}: {score.Score}"));
                await output.WriteLineAsync($"Puntuaciones: {formattedScores}");
                break;
        }
    }

    private static async Task WriteSnapshotAsync(GameSnapshot state, TextWriter output)
    {
        await output.WriteLineAsync($"Partida: {state.Name} ({state.Status})");
        foreach (var player in state.Players)
        {
            await output.WriteLineAsync($"Jugador {player.Number}: {player.Score}");
        }
    }
}
