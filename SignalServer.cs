﻿using System.Net.WebSockets;
using System.Text;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var clients = new ConcurrentDictionary<string, WebSocket>();
var offers = new ConcurrentDictionary<string, string>();
var answers = new ConcurrentDictionary<string, string>();

app.UseWebSockets();
app.Map("/ws", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        var ws = await context.WebSockets.AcceptWebSocketAsync();
        var clientId = Guid.NewGuid().ToString();
        clients.TryAdd(clientId, ws);
        Console.WriteLine($"Cliente conectado: {clientId}");

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var buffer = new byte[1024];
                var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                Console.WriteLine($"Mensagem recebida de {clientId}: {message}");

                // Corrigido para reconhecer "type":1 como oferta
                if (message.Contains("\"type\":1") || message.Contains("\"type\":\"offer\""))
                {
                    offers[clientId] = message;
                    Console.WriteLine($"Oferta armazenada para {clientId}");
                    foreach (var client in clients)
                    {
                        if (client.Key != clientId && client.Value.State == WebSocketState.Open)
                        {
                            await client.Value.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
                            Console.WriteLine($"Oferta enviada de {clientId} para {client.Key}");
                        }
                    }
                }
                // Corrigido para reconhecer "type":2 como resposta
                else if (message.Contains("\"type\":2") || message.Contains("\"type\":\"answer\""))
                {
                    answers[clientId] = message;
                    Console.WriteLine($"Resposta armazenada para {clientId}");
                    foreach (var client in clients)
                    {
                        if (client.Key != clientId && client.Value.State == WebSocketState.Open && offers.ContainsKey(client.Key))
                        {
                            await client.Value.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
                            Console.WriteLine($"Resposta enviada de {clientId} para {client.Key}");
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"Mensagem ignorada de {clientId}: não é oferta nem resposta");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro no WebSocket para {clientId}: {ex.Message}");
        }
        finally
        {
            clients.TryRemove(clientId, out _);
            offers.TryRemove(clientId, out _);
            answers.TryRemove(clientId, out _);
            if (ws.State != WebSocketState.Closed && ws.State != WebSocketState.Aborted)
            {
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Conexão fechada", CancellationToken.None);
            }
            Console.WriteLine($"Cliente desconectado: {clientId}");
        }
    }
});

await app.RunAsync("http://0.0.0.0:5000");