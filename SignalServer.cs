﻿using System;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace SignalServer
{
    public class SignalingMessage
    {
        public int Type { get; set; } // 1 = oferta, 2 = resposta
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

            // Mapeia a rota "/ws" para conexões WebSocket
            app.Map("/ws", async (HttpContext context) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    await context.Response.WriteAsync("Apenas requisições WebSocket são permitidas.");
                    return;
                }

                // Aceita a conexão WebSocket
                var ws = await context.WebSockets.AcceptWebSocketAsync();
                var clientId = Guid.NewGuid().ToString();
                Clients.TryAdd(clientId, ws);
                Log.Information("Cliente conectado: {ClientId}", clientId);

                // Envia ofertas existentes (menos de 5 minutos) para o novo cliente
                foreach (var offer in Offers)
                {
                    if (offer.Key != clientId &&
                        ws.State == WebSocketState.Open &&
                        (DateTime.UtcNow - offer.Value.Timestamp).TotalMinutes < 5)
                    {
                        await ws.SendAsync(Encoding.UTF8.GetBytes(offer.Value.Message),
                                             WebSocketMessageType.Text, true, CancellationToken.None);
                        Log.Information("Oferta existente enviada de {OfferClientId} para novo cliente {ClientId}", offer.Key, clientId);
                    }
                }

                try
                {
                    while (ws.State == WebSocketState.Open)
                    {
                        // Usa um método que acumula a mensagem completa
                        string message = await ReceiveFullMessageAsync(ws, CancellationToken.None);
                        if (string.IsNullOrWhiteSpace(message))
                        {
                            Log.Warning("Mensagem recebida de {ClientId} está vazia; ignorando.", clientId);
                            continue;
                        }

                        Log.Information("Mensagem recebida de {ClientId}: {Message}", clientId, message);

                        SignalingMessage? msg = null;
                        try
                        {
                            msg = JsonSerializer.Deserialize<SignalingMessage>(message);
                        }
                        catch (JsonException jsonEx)
                        {
                            Log.Error(jsonEx, "Erro ao desserializar mensagem de {ClientId}: {Error}", clientId, jsonEx.Message);
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
                                        await client.Value.SendAsync(Encoding.UTF8.GetBytes(message),
                                                                     WebSocketMessageType.Text, true, CancellationToken.None);
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
                                        await client.Value.SendAsync(Encoding.UTF8.GetBytes(message),
                                                                     WebSocketMessageType.Text, true, CancellationToken.None);
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
                    Log.Error(ex, "Erro no WebSocket para {ClientId}: {Error}", clientId, ex.Message);
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

        // Método para ler a mensagem completa do WebSocket (com buffer maior para evitar truncamento)
        private static async Task<string> ReceiveFullMessageAsync(WebSocket ws, CancellationToken cancellationToken)
        {
            // Aumentamos o tamanho do buffer para 8192 bytes
            var buffer = new byte[8192];
            using var ms = new System.IO.MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(ms.ToArray());
        }
    }
}
