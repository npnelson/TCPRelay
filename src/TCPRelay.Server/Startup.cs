using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace TCPRelay.Server
{
    public class Startup
    {
        private WebSocket _sender;
        private WebSocket _receiver;
        private TaskCompletionSource<object> _senderTask = new TaskCompletionSource<object>();
        private TaskCompletionSource<object> _receiverTask = new TaskCompletionSource<object>();
        // This method gets called by the runtime. Use this method to add services to the container.
        // For more information on how to configure your application, visit https://go.microsoft.com/fwlink/?LinkID=398940
        public void ConfigureServices(IServiceCollection services)
        {
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env,ILogger<Startup> logger)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            app.Use(async (context, next) =>
            {
                logger.LogInformation(context.Request.Scheme + context.Request.Method + context.Request.Path);
                await next();
            }
         );
            app.UseWebSockets();
            app.Use(async (context, next) =>
            {

                if (context.Request.Path.ToString().StartsWith("/ws"))
                {
                    if (context.WebSockets.IsWebSocketRequest)
                    {
                        WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync();
                        if (context.Request.Path.ToString().EndsWith("/sender"))
                        {
                            logger.LogInformation("Establishing Sender");
                            _sender = webSocket;
                            if (_sender != null && _receiver != null)
                            {

                          
                                var t1= Echo(_sender, _receiver, logger); ;
                                var t2 = Echo(_receiver, _sender, logger);
                                await Task.WhenAny(t1, t2);
                            }
                            else
                            {
                                await _senderTask.Task;
                            }
                        }
                        else
                        {
                            logger.LogInformation("Establishing Receiver");
                            _receiver = webSocket;
                            if (_sender != null && _receiver != null)
                            {
                                throw new NotImplementedException("Sender should establish after receiver");
                                await Echo(_sender, _receiver, logger);
                            }
                            else
                            {
                                await _receiverTask.Task;
                            }
                        }


                        //  await Echo(context, webSocket,logger);
                    }
                    else
                    {
                        logger.LogInformation("Not a web socket request");
                        context.Response.StatusCode = 400;
                    }
                }
                else
                {
                    await next();
                }

            });
        }


        private async Task Echo(WebSocket sender, WebSocket receiver, ILogger<Startup> logger)
        {

            var buffer = new byte[1024 * 4];
            WebSocketReceiveResult result = await sender.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            while (!result.CloseStatus.HasValue)
            {
                logger.LogInformation("Received Bytes On WebSocketServer");
                await receiver.SendAsync(new ArraySegment<byte>(buffer, 0, result.Count), result.MessageType, result.EndOfMessage, CancellationToken.None);

                result = await sender.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
            }
            await sender.CloseAsync(result.CloseStatus.Value, result.CloseStatusDescription, CancellationToken.None);
            logger.LogInformation("Disconnecting");
        }
    }
}
