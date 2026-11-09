// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace PCLCS.Link.Minecraft
{
    public sealed class ServerStatus
    {
        public ServerStatus(string version, string motd, int maxPlayers, string? protocol = null)
        {
            Version = version;
            Motd = motd;
            MaxPlayers = maxPlayers;
            Protocol = protocol;
        }

        public string Version { get; }
        public string Motd { get; }
        public int MaxPlayers { get; }
        public string? Protocol { get; }
    }

    public static class MinecraftProbe
    {
        public static async Task<bool> IsServerAsync(int port, TimeSpan timeout)
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                var exchange = ExchangeAsync(client, port);
                if (await Task.WhenAny(exchange, Task.Delay(timeout)).ConfigureAwait(false) != exchange)
                    return false;
                return await exchange.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException || ex is IOException)
            {
                return false;
            }
        }

        private static async Task<bool> ExchangeAsync(TcpClient client, int port)
        {
            await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 0xFE }, 0, 1).ConfigureAwait(false);
            var reply = new byte[1];
            return await stream.ReadAsync(reply, 0, 1).ConfigureAwait(false) == 1 && reply[0] == 0xFF;
        }

        public static async Task<ServerStatus?> GetStatusAsync(int port, TimeSpan timeout)
        {
            using var client = new TcpClient(AddressFamily.InterNetwork);
            try
            {
                var exchange = StatusExchangeAsync(client, port);
                if (await Task.WhenAny(exchange, Task.Delay(timeout)).ConfigureAwait(false) != exchange)
                    return null;
                return await exchange.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException || ex is ObjectDisposedException || ex is IOException)
            {
                return null;
            }
        }

        private static async Task<ServerStatus?> StatusExchangeAsync(TcpClient client, int port)
        {
            await client.ConnectAsync(IPAddress.Loopback, port).ConfigureAwait(false);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 0xFE, 0x01 }, 0, 2).ConfigureAwait(false);

            var head = await ReadExactlyAsync(stream, 3).ConfigureAwait(false);
            if (head == null || head[0] != 0xFF)
                return null;
            int length = (head[1] << 8) | head[2];
            var body = await ReadExactlyAsync(stream, length * 2).ConfigureAwait(false);
            if (body == null)
                return null;

            var parts = Encoding.BigEndianUnicode.GetString(body).Split('\0');
            if (parts.Length < 6 || parts[0] != "§1")
                return null;
            int.TryParse(parts[5], out int max);
            return new ServerStatus(parts[2], parts[3], max, parts[1]);
        }

        private static async Task<byte[]?> ReadExactlyAsync(Stream stream, int count)
        {
            var buffer = new byte[count];
            for (int read = 0; read < count;)
            {
                int n = await stream.ReadAsync(buffer, read, count - read).ConfigureAwait(false);
                if (n == 0)
                    return null;
                read += n;
            }
            return buffer;
        }
    }
}
