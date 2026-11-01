// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PCLCS.Link.Minecraft
{
    /// <summary>
    /// Sits between a joining player's game and the host's Minecraft world.
    /// </summary>
    public sealed class GameProxy : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly int _targetPort;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<TcpClient> _connections = new();

        private GameProxy(TcpListener listener, int targetPort)
        {
            _listener = listener;
            _targetPort = targetPort;
        }

        public static GameProxy Start(IPEndPoint bind, int targetPort)
        {
            var listener = new TcpListener(bind);
            listener.Start();
            var proxy = new GameProxy(listener, targetPort);
            _ = proxy.AcceptLoopAsync();
            return proxy;
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Func<string, string?>? Refuse { get; set; }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { }
            lock (_connections)
            {
                foreach (var c in _connections)
                {
                    try { c.Close(); } catch { }
                }
                _connections.Clear();
            }
        }

        private async Task AcceptLoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is ObjectDisposedException || ex is SocketException || ex is InvalidOperationException)
                {
                    return;
                }

                lock (_connections)
                    _connections.Add(client);

                _ = ForwardConnectionAsync(client);
            }
        }

        private async Task ForwardConnectionAsync(TcpClient client)
        {
            using (client)
            using (var server = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true })
            {
                try
                {
                    await server.ConnectAsync(IPAddress.Loopback, _targetPort).ConfigureAwait(false);
                    using var clientStream = client.GetStream();
                    using var serverStream = server.GetStream();

                    var t1 = clientStream.CopyToAsync(serverStream, 81920, _stop.Token);
                    var t2 = serverStream.CopyToAsync(clientStream, 81920, _stop.Token);

                    await Task.WhenAny(t1, t2).ConfigureAwait(false);
                }
                catch
                {
                }
                finally
                {
                    lock (_connections)
                        _connections.Remove(client);
                }
            }
        }
    }
}
