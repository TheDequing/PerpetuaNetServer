using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File("logs/server_logs.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

var builder = WebApplication.CreateBuilder(args);

// Configurar o Serilog como logger
builder.Host.UseSerilog();

var app = builder.Build();

// Habilita suporte a WebSockets
app.UseWebSockets();

// Dicionário para armazenar os clientes conectados
var connectedClients = new ConcurrentDictionary<string, WebSocket>();

// Endpoint para WebSocket
app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("Apenas requisições WebSocket são permitidas.");
        return;
    }

    // Aceita a conexão WebSocket
    var socket = await context.WebSockets.AcceptWebSocketAsync();
    var clientId = Guid.NewGuid().ToString();
    connectedClients.TryAdd(clientId, socket);
    Log.Information("Cliente conectado: {ClientId}", clientId);

    try
    {
        var buffer = new byte[4096];
        while (socket.State == WebSocketState.Open)
        {
            // Recebe a mensagem do cliente
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                Log.Information("Cliente {ClientId} solicitou fechamento da conexão.", clientId);
                break;
            }

            // Converte os bytes recebidos para string (esperando um JSON)
            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
            Log.Information("Mensagem recebida de {ClientId}: {Message}", clientId, message);

            // Aqui você pode desserializar o JSON, por exemplo:
            // var update = JsonSerializer.Deserialize<DatabaseUpdate>(message);
            // e tratar a atualização do banco de dados, se necessário.

            // Repassa a mensagem para todos os demais clientes conectados
            foreach (var kvp in connectedClients)
            {
                if (kvp.Key != clientId && kvp.Value.State == WebSocketState.Open)
                {
                    try
                    {
                        await kvp.Value.SendAsync(
                            Encoding.UTF8.GetBytes(message),
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None);
                        Log.Information("Mensagem encaminhada de {SourceClient} para {TargetClient}", clientId, kvp.Key);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "Erro ao enviar mensagem para {TargetClient}", kvp.Key);
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "Erro na conexão com o cliente {ClientId}", clientId);
    }
    finally
    {
        // Remove o cliente desconectado
        connectedClients.TryRemove(clientId, out _);
        if (socket.State != WebSocketState.Closed)
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Fechando conexão", CancellationToken.None);
        }
        Log.Information("Cliente desconectado: {ClientId}", clientId);
    }
});

app.Run();
