
    // Copyright (c) .NET Foundation. All rights reserved.
    // Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

    using System;
    using System.Buffers;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.Http.Connections.Client.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;
using Pipelines.Sockets.Unofficial;

namespace TCPRelay.SiteGateway
    {
        public class Program
        {
            public static async Task Main(string[] args)
            {
                var options = new SocketTransportOptions();
                var loggerFactory = LoggerFactory.Create(logging =>
                {
                    logging.AddConsole();
                    // Super verbose logs
                    logging.SetMinimumLevel(LogLevel.Trace);
                });

            RunWebSockets(loggerFactory);
                var logger = loggerFactory.CreateLogger<Program>();

                var listenerFactory = new SocketTransportFactory(Options.Create(options), loggerFactory);

                // A task to wait for shutdown
                var waitForShutdownTcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

                // A token that triggers once shutdown is fired to notify connections about shutdown
                using var shutdownTokenSource = new CancellationTokenSource();

                // Tracks all accepted connections (that are currently running)
                var connections = new ConcurrentDictionary<string, (ConnectionContext, Task)>();

                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    waitForShutdownTcs.TrySetResult(null);
                };

                // Bind to a random port
                var listener = await listenerFactory.BindAsync(new IPEndPoint(IPAddress.Loopback, 5002));

                logger.LogInformation("Listening on {EndPoint}", listener.EndPoint);

                // Dispose the listener so resources are cleaned ups
                await using (listener)
                {
                    // Start accepting connections
                    var acceptTask = AcceptConnectionsAsync(listener);

                    // Wait for a shutdown signal
                    await waitForShutdownTcs.Task;

                    // Trigger the token so connections can start shutting down
                    shutdownTokenSource.Cancel();

                    // Unbind so no new connections can be accepted
                    await listener.UnbindAsync();

                    // Wait for that loop to complete
                    await acceptTask;

                    // Wait for existing connections to end complete gracefully
                    var tasks = new List<Task>(connections.Count);
                    foreach (var pair in connections)
                    {
                        (_, var task) = pair.Value;
                        tasks.Add(task);
                    }

                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
                    {
                        if (await Task.WhenAll(tasks).WithCancellation(cts.Token))
                        {
                            // Everything ended
                            return;
                        }
                    }

                    // If all of the connections haven't finished, try aborting them all
                    foreach (var pair in connections)
                    {
                        (var connection, _) = pair.Value;
                        connection.Abort();
                    }

                    // Wait another second
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                    {
                        await Task.WhenAll(tasks).WithCancellation(cts.Token);
                    }
                }

                async Task AcceptConnectionsAsync(IConnectionListener listener)
                {
                    while (true)
                    {
                        var connection = await listener.AcceptAsync();

                        if (connection == null)
                        {
                            // The accept loop as been stopped
                            break;
                        }

                        var task = Task.Run(() => ExecuteConnectionAsync(connection, shutdownTokenSource.Token));
                        connections.TryAdd(connection.ConnectionId, (connection, task));
                    }
                }

                async Task ExecuteConnectionAsync(ConnectionContext connection, CancellationToken cancellationToken)
                {
                    logger.LogInformation("Accepted new connection {ConnectionId} from {RemoteEndPoint}", connection.ConnectionId, connection.RemoteEndPoint);



                #region Establish WebSocket
                var connectionOptions = new HttpConnectionOptions();
                connectionOptions.Url = new Uri("http://localhost:5000/ws/sender");
                connectionOptions.DefaultTransferFormat = TransferFormat.Binary;
                connectionOptions.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
                

                var webSocket = new WebSocketsTransport(connectionOptions, loggerFactory, null);
                #endregion

                try
                {
                    await webSocket.StartAsync(connectionOptions.Url, TransferFormat.Binary);
                }
                catch (Exception ex )
                {
                    logger.LogError(ex, "Exception thrown while attempting to start web socket");
                    throw ;
                }
                try
                {
                    
                        var input = connection.Transport.Input;
                        var output = webSocket.Output;

                        var t1= input.CopyToAsync(output, cancellationToken);
                    var t2 = webSocket.Input.CopyToAsync(connection.Transport.Output, cancellationToken);
                    await Task.WhenAny(t1, t2);
                    }
                    catch (OperationCanceledException)
                    {
                        // Not an issue, graceful shutdown started
                    }
                    finally
                    {
                        await connection.DisposeAsync();

                        connections.TryRemove(connection.ConnectionId, out _);

                        logger.LogInformation("Connection {ConnectionId} disconnected", connection.ConnectionId);
                    }
                }
            }
        private static async Task RunWebSockets(ILoggerFactory loggerFactory)
        {
            //    var client = new ClientWebSocket();

            var hostEntry = Dns.GetHostEntry("localhost");
            var ipe = new IPEndPoint(hostEntry.AddressList.Single(x => x.AddressFamily == AddressFamily.InterNetwork), 5005);
            var socket = await SocketConnection.ConnectAsync(ipe);

            var connectionOptions = new HttpConnectionOptions();
            connectionOptions.Url = new Uri("http://localhost:5000/ws/receiver");
            connectionOptions.DefaultTransferFormat = TransferFormat.Binary;
            connectionOptions.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
            var serviceCollection = new ServiceCollection();
            serviceCollection.AddLogging(builder => builder.AddConsole());
            var serviceProvider = serviceCollection.BuildServiceProvider();
           

            var webSocket = new WebSocketsTransport(connectionOptions, loggerFactory, null);


            try
            {
                await webSocket.StartAsync(connectionOptions.Url, TransferFormat.Binary);
                //   var shutdown = new TaskCompletionSource<object>();
                var inputTask= webSocket.Input.CopyToAsync(socket.Output);
                var outputTask = socket.Input.CopyToAsync(webSocket.Output);
                await Task.WhenAny(inputTask, outputTask);
             //   _ = SendLoop(webSocket.Input, null);
               //_ = ReceiveLoop(webSocket.Output, socket.Input);
              //  await shutdown.Task;
            }
            catch (Exception ex)
            {
                throw ex;
            }
            finally
            {

                await webSocket.StopAsync();
               // socket.Dispose();
            }
            //      await client.ConnectAsync(new Uri("ws://localhost:5000/ws"), CancellationToken.None);
            Console.WriteLine("Receiver Connected!");

            //initiate Tcp Connection to HL7 on localhost:5005



            // socket.


            //  await Receiving(client);


        }
        private static async Task ReceiveLoop(PipeWriter output, PipeReader input)
        {
            while (true)
            {
                var result = await input.ReadAsync();
                var buffer = result.Buffer;

                try
                {
                    if (!buffer.IsEmpty)
                    {
                        foreach (var seg in buffer)
                        {
                            await output.WriteAsync(seg);
                        }
                    }
                    else if (result.IsCompleted)
                    {
                        // No more data, and the pipe is complete
                        break;
                    }
                }
                finally
                {
                    input.AdvanceTo(buffer.End);
                }
            }
        }

        private static async Task SendLoop(PipeReader input, PipeWriter output)
        {
            while (true)
            {
                var result = await input.ReadAsync();
                foreach (var seg in result.Buffer)
                {
                    //  await output.WriteAsync(seg);
                    Console.WriteLine(System.Text.Encoding.ASCII.GetString(seg.ToArray()));
                    
                }

            }
        }
    }

        public static class TaskExtensions
        {
            public static async Task<bool> WithCancellation(this Task task, CancellationToken cancellationToken)
            {
                var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

                // This disposes the registration as soon as one of the tasks trigger
                using (cancellationToken.Register(state =>
                {
                    ((TaskCompletionSource<object>)state).TrySetResult(null);
                },
                tcs))
                {
                    var resultTask = await Task.WhenAny(task, tcs.Task);
                    if (resultTask == tcs.Task)
                    {
                        // Operation cancelled
                        return false;
                    }

                    await task;
                    return true;
                }
            }
        }
    }

