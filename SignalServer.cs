using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using SIPSorcery.Net;

namespace PerpetuaNet
{
    public class SignalingMessage
    {
        public int Type { get; set; } // 1 = oferta, 2 = resposta
        public string? Sdp { get; set; }
    }

    public class WebRTCSyncService : IDisposable
    {
        private RTCPeerConnection? _pc;
        private ClientWebSocket? _ws;
        private readonly string _logFile = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs.txt");
        private bool _disposed = false;

        public WebRTCSyncService()
        {
            // Configuração do Serilog
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.File(_logFile, rollingInterval: RollingInterval.Day)
                .CreateLogger();
        }

        public async Task InitializeAndSync()
        {
            Log.Information("Aplicativo iniciado, preparando sincronização WebRTC...");

            try
            {
                Log.Information("Iniciando sincronização WebRTC...");

                // Configurar a RTCConfiguration com um servidor STUN
                var config = new RTCConfiguration
                {
                    iceServers = new System.Collections.Generic.List<RTCIceServer>
                    {
                        new RTCIceServer { urls = "stun:stun.l.google.com:19302" }
                    }
                };

                Log.Information("Configurando RTCPeerConnection...");
                _pc = new RTCPeerConnection(config);

                // Criação do canal de dados
                Log.Information("Criando canal de dados 'syncChannel'...");
                var channel = await _pc.createDataChannel("syncChannel", null);
                channel.onopen += () =>
                {
                    channel.send("Sincronização iniciada!");
                    Log.Information("Canal de dados aberto.");
                };
                channel.onmessage += (ch, protocol, data) =>
                {
                    Log.Information("Mensagem recebida no canal: {Data}", Encoding.UTF8.GetString(data));
                };

                // Inicialização do ClientWebSocket
                Log.Information("Inicializando ClientWebSocket...");
                _ws = new ClientWebSocket();
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
                Log.Information("Conectando ao WebSocket em: wss://perpetuanetserver.onrender.com/ws");

                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    await _ws.ConnectAsync(new Uri("wss://perpetuanetserver.onrender.com/ws"), cts.Token);
                }
                Log.Information("WebRTC: Conectado ao WebSocket.");

                // Configurar manipulador de candidatos ICE
                _pc.onicecandidate += async (candidate) =>
                {
                    await SendIceCandidateAsync(candidate);
                };

                // Aguardar oferta remota por até 30 segundos
                RTCSessionDescriptionInit? remoteOffer = null;
                Log.Information("Verificando se há oferta remota...");
                using (var offerCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    while (_ws.State == WebSocketState.Open && remoteOffer == null && !offerCts.Token.IsCancellationRequested)
                    {
                        string message = await ReceiveFullMessageAsync(_ws, offerCts.Token);
                        Log.Information("Mensagem recebida do servidor: {Message}", message);

                        try
                        {
                            var msg = JsonSerializer.Deserialize<SignalingMessage>(message);
                            if (msg?.Type == 1 && !string.IsNullOrWhiteSpace(msg.Sdp))
                            {
                                remoteOffer = new RTCSessionDescriptionInit { type = RTCSdpType.offer, sdp = msg.Sdp };
                                Log.Information("Oferta remota recebida, configurando descrição remota...");
                                await _pc.setRemoteDescription(new RTCSessionDescription { type = RTCSdpType.offer, sdp = msg.Sdp });
                                Log.Information("Oferta remota configurada com sucesso.");
                                break;
                            }
                            else
                            {
                                Log.Warning("Mensagem ignorada: não é uma oferta válida.");
                            }
                        }
                        catch (JsonException jex)
                        {
                            Log.Error(jex, "Erro ao desserializar mensagem de oferta.");
                        }
                    }
                }

                if (remoteOffer == null)
                {
                    // Se não houver oferta remota, criar oferta local
                    Log.Information("Nenhuma oferta remota recebida. Criando oferta local...");
                    var offer = await _pc.createOffer(null);
                    await _pc.setLocalDescription(offer);
                    Log.Information("Oferta local criada e configurada.");

                    var offerJson = JsonSerializer.Serialize(new SignalingMessage { Type = 1, Sdp = offer.sdp });
                    Log.Information("Enviando oferta local ao servidor...");
                    await _ws.SendAsync(Encoding.UTF8.GetBytes(offerJson), WebSocketMessageType.Text, true, CancellationToken.None);
                    Log.Information("Oferta local enviada com sucesso.");
                }
                else
                {
                    // Se houver oferta, criar e enviar resposta
                    Log.Information("Criando resposta para a oferta remota...");
                    var answer = await _pc.createAnswer(null);
                    await _pc.setLocalDescription(answer);
                    Log.Information("Resposta local criada e configurada.");

                    var answerJson = JsonSerializer.Serialize(new SignalingMessage { Type = 2, Sdp = answer.sdp });
                    Log.Information("Enviando resposta ao servidor...");
                    await _ws.SendAsync(Encoding.UTF8.GetBytes(answerJson), WebSocketMessageType.Text, true, CancellationToken.None);
                    Log.Information("Resposta enviada com sucesso.");
                }

                // Aguardar resposta adicional por até 30 segundos
                Log.Information("Aguardando resposta adicional ou conexão final...");
                using (var responseCts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    while (_ws.State == WebSocketState.Open && !responseCts.Token.IsCancellationRequested)
                    {
                        string additional = await ReceiveFullMessageAsync(_ws, responseCts.Token);
                        if (string.IsNullOrWhiteSpace(additional))
                        {
                            Log.Warning("Mensagem adicional vazia recebida; ignorando.");
                            continue;
                        }

                        try
                        {
                            var msg = JsonSerializer.Deserialize<SignalingMessage>(additional);
                            if (msg?.Type == 2 && !string.IsNullOrWhiteSpace(msg.Sdp))
                            {
                                Log.Information("Configurando descrição remota com a resposta...");
                                await _pc.setRemoteDescription(new RTCSessionDescription { type = RTCSdpType.answer, sdp = msg.Sdp });
                                Log.Information("Resposta remota configurada com sucesso.");
                                break;
                            }
                            else
                            {
                                Log.Warning("Mensagem adicional ignorada: não é uma resposta válida.");
                            }
                        }
                        catch (JsonException jex)
                        {
                            Log.Error(jex, "Erro ao desserializar mensagem adicional.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Erro na inicialização do WebRTC: {Message}", ex.Message);
            }
        }

        private async Task SendIceCandidateAsync(RTCIceCandidate candidate)
        {
            if (_ws != null && _ws.State == WebSocketState.Open)
            {
                var json = JsonSerializer.Serialize(candidate);
                await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
                Log.Information("Candidato ICE enviado: {Candidate}", json);
            }
            else
            {
                Log.Warning("WebSocket não está aberto para enviar candidato ICE.");
            }
        }

        // Método para receber a mensagem completa do WebSocket com buffer ampliado
        private static async Task<string> ReceiveFullMessageAsync(WebSocket ws, CancellationToken cancellationToken)
        {
            // Aumentamos o buffer para 16KB (16384 bytes)
            var buffer = new byte[16384];
            using var ms = new System.IO.MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                ms.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            return Encoding.UTF8.GetString(ms.ToArray());
        }

        public void Dispose()
        {
            if (_disposed) return;
            try
            {
                if (_ws != null && (_ws.State == WebSocketState.Open || _ws.State == WebSocketState.CloseReceived))
                {
                    _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Fechando conexão", CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                _ws?.Dispose();
                if (_pc != null)
                {
                    _pc.Close("Serviço finalizado");
                    _pc.Dispose();
                }
                Log.CloseAndFlush();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Erro ao liberar recursos: {Message}", ex.Message);
            }
            _disposed = true;
        }
    }
}
