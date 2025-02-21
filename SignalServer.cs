using System.Net.WebSockets;
using System.Text;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var clients = new ConcurrentDictionary<string, WebSocket>();

app.UseWebSockets();
app.Map("/ws", async context =>
{
    if (context.WebSockets.IsWebSocketRequest)
    {
        var ws = await context.WebSockets.AcceptWebSocketAsync();
        var clientId = Guid.NewGuid().ToString();
        clients.TryAdd(clientId, ws);

        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var buffer = new byte[1024];
                var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
                var message = Encoding.UTF8.GetString(buffer, 0, result.Count);

                foreach (var client in clients)
                {
                    if (client.Key != clientId && client.Value.State == WebSocketState.Open)
                    {
                        await client.Value.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Erro no WebSocket: {ex.Message}");
        }
        finally
        {
            clients.TryRemove(clientId, out _);
            await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Conexão fechada", CancellationToken.None);
        }
    }
});

await app.RunAsync("http://0.0.0.0:5000");