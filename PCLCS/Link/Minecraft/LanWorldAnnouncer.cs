// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PCLCS.Link.Minecraft
{
    /// <summary>
    /// Makes the host's world appear under "Scan for LAN worlds" in the joining player's multiplayer screen.
    /// </summary>
    public sealed class LanWorldAnnouncer : IDisposable
    {
        private static readonly IPAddress Group = IPAddress.Parse("224.0.2.60");
        private const int AnnouncePort = 4445;
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(1500);

        private readonly CancellationTokenSource _stop = new();
        private readonly Func<string> _motd;
        private readonly int _port;
        private readonly bool _localOnly;

        public LanWorldAnnouncer(Func<string> motd, int port, bool localOnly)
        {
            _motd = motd;
            _port = port;
            _localOnly = localOnly;
            _ = RunAsync();
        }

        public void Dispose()
        {
            _stop.Cancel();
        }

        public static string Format(string motd, int port)
        {
            return "[MOTD]" + motd + "[/MOTD][AD]" + port + "[/AD]";
        }

        private async Task RunAsync()
        {
            var target = _localOnly ? new IPEndPoint(IPAddress.Loopback, AnnouncePort) : new IPEndPoint(Group, AnnouncePort);
            var sockets = _localOnly
                ? new List<Socket> { new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) }
                : CreateMulticastSenders();

            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    byte[] message = Encoding.UTF8.GetBytes(Format(_motd(), _port));
                    foreach (var socket in sockets)
                    {
                        try
                        {
                            socket.SendTo(message, target);
                        }
                        catch (SocketException)
                        {
                        }
                    }

                    try
                    {
                        await Task.Delay(Interval, _stop.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
            finally
            {
                foreach (var socket in sockets)
                    socket.Close();
            }
        }

        private static List<Socket> CreateMulticastSenders()
        {
            var sockets = new List<Socket>();
            foreach (var address in NetworkCheck.GetIPv4Addresses())
            {
                try
                {
                    var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, address.GetAddressBytes());
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
                    sockets.Add(socket);
                }
                catch (SocketException)
                {
                }
            }
            return sockets;
        }
    }
}
