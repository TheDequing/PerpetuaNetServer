using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace SignalServer
{
    public class SignalingMessage
    {
        public int Type { get; set; } // 1 = offer, 2 = answer
        public string? Sdp { get; set; }
    }

    public class Program
    {
        private static readonly ConcurrentDictionary<string, WebSocket> Clients = new();
        private static readonly ConcurrentDictionary<string, (string Message, DateTime Timestamp)> Offers = new();

        public static async Task Main(string[] args)
        {
            // Configuração do Serilog
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console()
                .WriteTo.File("logs/server_logs.txt", rollingInterval: RollingInterval.Day)
                .CreateLogger();

            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();

            app.UseWebSockets();

            app.Map("/ws", async context =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                var ws = await context.WebSockets.AcceptWebSocketAsync();
                var clientId = Guid.NewGuid().ToString();
                Clients.TryAdd(clientId, ws);
                Log.Information("Cliente conectado: {ClientId}", clientId);

                // Enviar ofertas existentes (com menos de 5 minutos) para o novo cliente
                foreach (var offer in Offers)
                {
                    if (offer.Key != clientId && ws.State == WebSocketState.Open &&
                        (DateTime.UtcNow - offer.Value.Timestamp).TotalMinutes < 5)
                    {
                        await ws.SendAsync(Encoding.UTF8.GetBytes(offer.Value.Message), WebSocketMessageType.Text, true, CancellationToken.None);
                        Log.Information("Oferta existente enviada de {OfferClientId} para novo cliente {ClientId}", offer.Key, clientId);
                    }
                }

                try
                {
                    while (ws.State == WebSocketState.Open)
                    {
                        var buffer = new byte[4096];
                        var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

                        // Verifica se o resultado contém dados
                        if (result.Count == 0)
                        {
                            Log.Warning("Mensagem recebida de {ClientId} está vazia; ignorando.", clientId);
                            continue;
                        }

                        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        Log.Information("Mensagem recebida de {ClientId}: {Message}", clientId, message);

                        // Tenta desserializar a mensagem; se falhar, registra e ignora
                        SignalingMessage? msg = null;
                        try
                        {
                            msg = JsonSerializer.Deserialize<SignalingMessage>(message);
                        }
                        catch (JsonException jsonEx)
                        {
                            Log.Error(jsonEx, "Erro na desserialização da mensagem de {ClientId}: {Message}", clientId, jsonEx.Message);
                            continue;
                        }

                        if (msg == null || string.IsNullOrWhiteSpace(msg.Sdp))
                        {
                            Log.Warning("Mensagem ignorada de {ClientId}: formato inválido", clientId);
                            continue;
                        }

                        if (msg.Type == 1) // Oferta
                        {
                            Offers[clientId] = (message, DateTime.UtcNow);
                            Log.Information("Oferta armazenada para {ClientId}", clientId);
                            foreach (var client in Clients)
                            {
                                if (client.Key != clientId && client.Value.State == WebSocketState.Open)
                                {
                                    try
                                    {
                                        await client.Value.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
                                        Log.Information("Oferta enviada de {ClientId} para {TargetClientId}", clientId, client.Key);
                                    }
                                    catch (WebSocketException wsEx)
                                    {
                                        Log.Error(wsEx, "Erro ao enviar oferta de {ClientId} para {TargetClientId}", clientId, client.Key);
                                    }
                                }
                            }
                        }
                        else if (msg.Type == 2) // Resposta
                        {
                            foreach (var client in Clients)
                            {
                                if (client.Key != clientId && client.Value.State == WebSocketState.Open && Offers.ContainsKey(client.Key))
                                {
                                    try
                                    {
                                        await client.Value.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
                                        Log.Information("Resposta enviada de {ClientId} para {TargetClientId}", clientId, client.Key);
                                    }
                                    catch (WebSocketException wsEx)
                                    {
                                        Log.Error(wsEx, "Erro ao enviar resposta de {ClientId} para {TargetClientId}", clientId, client.Key);
                                    }
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
                    Clients.TryRemove(clientId, out _);
                    Offers.TryRemove(clientId, out _);
                    if (ws.State != WebSocketState.Closed && ws.State != WebSocketState.Aborted)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Conexão fechada", CancellationToken.None);
                    }
                    Log.Information("Cliente desconectado: {ClientId}", clientId);
                }
            });

            await app.RunAsync("http://0.0.0.0:5000");
        }
    }
}
