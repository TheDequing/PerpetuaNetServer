using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Serilog;
using SIPSorcery.Net; // Certifique-se de que este pacote está referenciado (versão 8.0.9)

namespace SignalServer
{
    // Classe que representa a mensagem de sinalização
    public class SignalingMessage
    {
        public int Type { get; set; } // 1 = offer, 2 = answer
        public string? Sdp { get; set; }
    }

    public class Program
    {
        // Ponto de entrada do programa
        public static async Task Main(string[] args)
        {
            // Configuração do Serilog para logging
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console()
                .WriteTo.File("logs/server_logs.txt", rollingInterval: RollingInterval.Day)
                .CreateLogger();

            var builder = WebApplication.CreateBuilder(args);
            var app = builder.Build();

            // Habilita o middleware para WebSockets
            app.UseWebSockets();

            // Mapeia a rota "/ws" para conexões WebSocket
            app.Map("/ws", async context =>
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    using WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();
                    Log.Information("Cliente conectado: {ConnectionId}", Guid.NewGuid());
                    await ProcessWebSocketAsync(webSocket);
                }
                else
                {
                    context.Response.StatusCode = 400;
                }
            });

            await app.RunAsync();
        }

        // Método para processar a comunicação via WebSocket
        private static async Task ProcessWebSocketAsync(WebSocket ws)
        {
            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    string message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    Log.Information("Mensagem recebida: {Message}", message);

                    try
                    {
                        // Deserializa a mensagem de sinalização
                        var sigMsg = JsonSerializer.Deserialize<SignalingMessage>(message);
                        if (sigMsg != null && !string.IsNullOrEmpty(sigMsg.Sdp))
                        {
                            // Se a propriedade Sdp (string) precisa ser convertida para o tipo SDP, usamos o ParseSDP:
                            SDP sdpObj = SDP.ParseSDP(sigMsg.Sdp);
                            Log.Information("SDP parseado com sucesso.");
                            // Aqui você pode continuar o processamento, por exemplo,
                            // configurando uma oferta ou resposta para o WebRTC.
                        }
                    }
                    catch (JsonException jsonEx)
                    {
                        Log.Error(jsonEx, "Erro ao deserializar mensagem de sinalização.");
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Encerrando", CancellationToken.None);
                    Log.Information("Conexão WebSocket encerrada pelo cliente.");
                }
            }
        }

        // Exemplo de como criar e configurar uma oferta com RTC (correção da conversão de string para SDP):
        private static RTCSessionDescription CreateOffer(string sdpOfferString)
        {
            // Supondo que o método de parsing retorne um objeto do tipo SDP
            SDP parsedSdp = SDP.ParseSDP(sdpOfferString);
            return new RTCSessionDescription
            {
                type = RTCSdpType.offer,
                sdp = parsedSdp  // Aqui convertemos a string para o tipo SDP
            };
        }

        // Similarmente, para criar uma resposta, você faria:
        private static RTCSessionDescription CreateAnswer(string sdpAnswerString)
        {
            SDP parsedSdp = SDP.ParseSDP(sdpAnswerString);
            return new RTCSessionDescription
            {
                type = RTCSdpType.answer,
                sdp = parsedSdp
            };
        }
    }
}
