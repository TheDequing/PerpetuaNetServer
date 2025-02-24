using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.SignalR;
using System;
using System.Threading.Tasks;

namespace ReplicacaoServidor
{
    // Hub SignalR para replicação de atualizações
    public class ReplicacaoHub : Hub
    {
        /// <summary>
        /// Método chamado pelos clientes para enviar atualizações (por exemplo, alterações no banco de dados).
        /// A mensagem é encaminhada para todos os outros clientes.
        /// </summary>
        /// <param name="atualizacao">Conteúdo da atualização (pode ser um JSON ou outro formato)</param>
        public async Task EnviarAtualizacao(string atualizacao)
        {
            Console.WriteLine($"Atualização recebida do cliente {Context.ConnectionId}: {atualizacao}");
            // Envia a atualização para todos os clientes, exceto aquele que enviou
            await Clients.Others.SendAsync("ReceberAtualizacao", atualizacao);
        }

        public override async Task OnConnectedAsync()
        {
            Console.WriteLine($"Cliente conectado: {Context.ConnectionId}");
            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception exception)
        {
            Console.WriteLine($"Cliente desconectado: {Context.ConnectionId}");
            await base.OnDisconnectedAsync(exception);
        }
    }

    public class Program
    {
        public static void Main(string[] args)
        {
            // Cria o builder do aplicativo web
            var builder = WebApplication.CreateBuilder(args);

            // Adiciona os serviços do SignalR
            builder.Services.AddSignalR();

            // Cria o aplicativo
            var app = builder.Build();

            // Configura o roteamento
            app.UseRouting();

            // Mapeia o Hub em uma rota (por exemplo, "/replicacao")
            app.UseEndpoints(endpoints =>
            {
                endpoints.MapHub<ReplicacaoHub>("/replicacao");
            });

            // Inicia o servidor
            app.Run();
        }
    }
}
