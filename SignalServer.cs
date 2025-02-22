using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Serilog;
using System;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// Configuração do Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File("logs/server_logs.txt", rollingInterval: RollingInterval.Day)
    .CreateLogger();

var clients = new ConcurrentDictionary<string, WebSocket>();
var offers = new ConcurrentDictionary<string, (string Message, DateTime Timestamp)>();

public class SignalingMessage
{
    public int Type { get; set; } // 1 = offer, 2 = answer
    public string? Sdp { get; set; }
}

app.UseWebSockets();
app.Map("/ws", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        var ws = await context.WebSockets.AcceptWebSocketAsync();
        var clientId = Guid.NewGuid().ToString();
        clients.TryAdd(clientId, ws);
        Log.Information("Cliente conectado: {ClientId}", clientId);

        // Enviar oferta existente de outro peer, se houver (com limite de 5 minutos)
        foreach (var offer in offers)
        {
            if (offer.Key != clientId && ws.State == WebSocketState.Open && (DateTime.UtcNow - offer.Value.Timestamp).TotalMinutes < 5)
            {
                await ws.SendAsync(Encoding.UTF8.GetBytes(offer.Value.Message), WebSocketMessageType.Text, true, CancellationToken.None);
                Log.Information("Oferta existente enviada de {OfferClientId} para novo cliente {ClientId}", offer.Key, clientId);
            }
        }

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var buffer = new byte[1024];
                var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Log.Information("Mensagem recebida de {ClientId}: {Message}", clientId, message);

                var msg = JsonSerializer.Deserialize<SignalingMessage>(message);
                if (msg == null || string.IsNullOrEmpty(msg.Sdp))
                {
                    Log.Warning("Mensagem ignorada de {ClientId}: formato inválido", clientId);
                    continue;
                }

                if (msg.Type == 1) // Oferta
                {
                    offers[clientId] = (message, DateTime.UtcNow);
                    Log.Information("Oferta armazenada para {ClientId}", clientId);
                    foreach (var client in clients)
                    {
                        if (client.Key != clientId && client.Value.State == WebSocketState.Open)
                        {
                            await client.Value.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
                            Log.Information("Oferta enviada de {ClientId} para {TargetClientId}", clientId, client.Key);
                        }
                    }
                }
                else if (msg.Type == 2) // Resposta
                {
                    foreach (var client in clients)
                    {
                        if (client.Key != clientId && client.Value.State == WebSocketState.Open && offers.ContainsKey(client.Key))
                        {
                            await client.Value.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
                            Log.Information("Resposta enviada de {ClientId} para {TargetClientId}", clientId, client.Key);
                        }
                    }
                }
                else
                {
                    Log.Warning("Mensagem ignorada de {ClientId}: tipo desconhecido {Type}", clientId, msg.Type);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Erro no WebSocket para {ClientId}: {Message}", clientId, ex.Message);
        }
        finally
        {
            clients.TryRemove(clientId, out _);
            offers.TryRemove(clientId, out _);
            if (ws.State != WebSocketState.Closed && ws.State != WebSocketState.Aborted)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Conexão fechada", CancellationToken.None);
            }
            Log.Information("Cliente desconectado: {ClientId}", clientId);
        }
    }
});

await app.RunAsync("http://0.0.0.0:5000");