
    // Copyright (c) .NET Foundation. All rights reserved.
    // Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

    using System;
    using System.Buffers;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Net;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Connections;
    using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
    using Microsoft.Extensions.Logging;
    using Microsoft.Extensions.Options;

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
                var listener = await listenerFactory.BindAsync(new IPEndPoint(IPAddress.Loopback, 0));

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


                
                    try
                    {
                    
                        var input = connection.Transport.Input;
                        var output = connection.Transport.Output;

                        await input.CopyToAsync(output, cancellationToken);
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

