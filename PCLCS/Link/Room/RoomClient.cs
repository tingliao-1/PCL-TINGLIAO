// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PCLCS.Link.Minecraft;

namespace PCLCS.Link.Room
{
    public sealed class RoomClient : IDisposable
    {
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(64);

        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly SemaphoreSlim _lock = new(1, 1);

        public bool IsAdmin { get; private set; }
        public string? AdminToken { get; private set; }

        private RoomClient(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        public static async Task<RoomClient?> TryConnectAsync(int localPort, CancellationToken cancellation)
        {
            var client = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
            RoomClient? room = null;
            try
            {
                var connect = client.ConnectAsync(IPAddress.Loopback, localPort);
                if (await Task.WhenAny(connect, Task.Delay(TimeSpan.FromSeconds(5), cancellation)).ConfigureAwait(false) != connect)
                    return null;
                await connect.ConfigureAwait(false);

                room = new RoomClient(client);
                var response = await room.SendAsync(RoomProtocol.Ping, RoomProtocol.Fingerprint, TimeSpan.FromSeconds(8), cancellation)
                    .ConfigureAwait(false);
                if (response.IsOk && response.Body.SequenceEqual(RoomProtocol.Fingerprint))
                {
                    var result = room;
                    room = null;
                    return result;
                }
                return null;
            }
            catch (Exception ex) when (IsConnectionError(ex))
            {
                return null;
            }
            finally
            {
                if (room != null)
                    room.Dispose();
                else if (!client.Connected)
                    client.Close();
            }
        }

        public async Task<int> GetServerPortAsync(CancellationToken cancellation)
        {
            var response = await SendAsync(RoomProtocol.ServerPort, null, RequestTimeout, cancellation).ConfigureAwait(false);
            if (!response.IsOk || response.Body.Length != 2)
                throw new InvalidDataException("The host has no game running (status " + response.Status + ").");
            return (response.Body[0] << 8) | response.Body[1];
        }

        public async Task<GameVersionInfo?> GetGameVersionAsync(CancellationToken cancellation)
        {
            var response = await SendAsync(RoomProtocol.GameVersion, null, RequestTimeout, cancellation).ConfigureAwait(false);
            if (!response.IsOk || response.Body == null || response.Body.Length == 0)
                return null;
            string text = Encoding.UTF8.GetString(response.Body);
            return GameVersionInfo.Parse(text);
        }

        public async Task SendHeartbeatAsync(PlayerProfile self, CancellationToken cancellation)
        {
            string json = Json.Serialize(self.ToJson(includeKind: false));
            var response = await SendAsync(RoomProtocol.PlayerPing, Encoding.UTF8.GetBytes(json), RequestTimeout, cancellation)
                .ConfigureAwait(false);
            if (!response.IsOk)
            {
                string reply = Encoding.UTF8.GetString(response.Body);
                if (reply == RoomServer.RemovedReply || reply == RoomServer.BlockedReply)
                    throw new RoomRejectedException(reply == RoomServer.BlockedReply);
                if (reply == RoomServer.LockedReply)
                    throw new LinkException("Link.Error.RoomLocked");
                if (reply == RoomServer.WhitelistRequiredReply)
                    throw new LinkException("Link.Error.WhitelistRequired");
                if (reply == RoomServer.WhitelistDeviceMismatchReply)
                    throw new LinkException("Link.Error.WhitelistDeviceMismatch");
                if (reply == RoomServer.PremiumRequiredReply)
                    throw new LinkException("Link.Error.PremiumRequired");
                throw new InvalidDataException("Heartbeat rejected: " + reply);
            }

            if (response.Body != null && response.Body.Length > 0)
            {
                try
                {
                    if (Json.Parse(Encoding.UTF8.GetString(response.Body)) is IDictionary<string, object> replyObj)
                    {
                        string role = replyObj.TryGetValue("role", out var r) ? r?.ToString() ?? "" : "";
                        IsAdmin = string.Equals(role, "ADMIN", StringComparison.OrdinalIgnoreCase);
                        AdminToken = replyObj.TryGetValue("admin_token", out var t) ? t?.ToString() : null;
                    }
                }
                catch
                {
                    // Ignore body parse errors for backwards compatibility
                }
            }
        }

        public async Task SendAdminKickAsync(string token, string targetMachineId, CancellationToken cancellation)
        {
            var payload = new Dictionary<string, object>
            {
                ["token"] = token,
                ["action"] = "kick",
                ["target"] = targetMachineId
            };
            string json = Json.Serialize(payload);
            var response = await SendAsync(RoomProtocol.AdminAction, Encoding.UTF8.GetBytes(json), RequestTimeout, cancellation)
                .ConfigureAwait(false);
            if (!response.IsOk)
            {
                string err = response.Body != null && response.Body.Length > 0 ? Encoding.UTF8.GetString(response.Body) : "Unknown error";
                throw new InvalidOperationException(err);
            }
        }

        public async Task<IReadOnlyList<PlayerProfile>> GetPlayersAsync(CancellationToken cancellation)
        {
            var response = await SendAsync(RoomProtocol.PlayerList, null, RequestTimeout, cancellation).ConfigureAwait(false);
            if (!response.IsOk)
                throw new InvalidDataException("Player list rejected.");

            if (!(Json.TryParse(Encoding.UTF8.GetString(response.Body)) is object[] items))
                throw new InvalidDataException("Player list is not an array.");

            return items.OfType<IDictionary<string, object>>().Select(item => PlayerProfile.FromJson(item, PlayerKind.Guest)).ToList();
        }

        public void Dispose()
        {
            _client.Close();
            _lock.Dispose();
        }

        public static bool IsConnectionError(Exception ex)
        {
            return ex is IOException || ex is SocketException || ex is ObjectDisposedException || ex is InvalidDataException
                   || ex is TimeoutException || ex is ArgumentException || ex is InvalidOperationException;
        }

        private async Task<RoomFraming.Response> SendAsync(string kind, byte[]? body, TimeSpan timeout, CancellationToken cancellation)
        {
            await _lock.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                using (expiry.Token.Register(_client.Close))
                {
                    expiry.CancelAfter(timeout);
                    try
                    {
                        await RoomFraming.WriteRequestAsync(_stream, kind, body, expiry.Token).ConfigureAwait(false);
                        return await RoomFraming.ReadResponseAsync(_stream, expiry.Token).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (expiry.IsCancellationRequested && !cancellation.IsCancellationRequested)
                    {
                        throw new TimeoutException("The host did not answer " + kind + ".", ex);
                    }
                }
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    public sealed class RoomRejectedException : LinkException
    {
        public RoomRejectedException(bool blocked)
            : base(blocked ? "Link.Error.RoomBlocked" : "Link.Error.RoomRemoved")
        {
            Blocked = blocked;
        }

        public bool Blocked { get; }
    }
}
