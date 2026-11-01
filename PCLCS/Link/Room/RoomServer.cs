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

namespace PCLCS.Link.Room
{
    public sealed class RoomServer : IDisposable
    {
        public const int PreferredPort = 13448;
        private static readonly TimeSpan GuestTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(64);
        public static readonly TimeSpan RemovalTime = TimeSpan.FromMinutes(1);

        public const string RemovedReply = "campfire:removed";
        public const string BlockedReply = "campfire:blocked";
        public const string LockedReply = "campfire:locked";
        public const string PremiumRequiredReply = "campfire:premium_required";

        public bool IsLocked { get; set; }
        public bool PremiumOnly { get; set; }

        private readonly byte[] _protocolList;
        private PlayerProfile _host;
        private readonly int _minecraftPort;
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly List<Guest> _guests = new();
        private readonly Dictionary<string, DateTime> _removed = new();

        private RoomServer(PlayerProfile host, int minecraftPort, TcpListener listener)
        {
            _host = host;
            _minecraftPort = minecraftPort;
            _listener = listener;

            var kinds = new[]
            {
                RoomProtocol.Ping, RoomProtocol.Protocols, RoomProtocol.ServerPort, RoomProtocol.PlayerPing, RoomProtocol.PlayerList,
            };
            _protocolList = Encoding.UTF8.GetBytes(string.Join("\0", kinds));
        }

        public event EventHandler? PlayersChanged;

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public static RoomServer Start(PlayerProfile host, int minecraftPort)
        {
            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, PreferredPort);
                listener.Start();
            }
            catch (SocketException)
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
            }

            var server = new RoomServer(host, minecraftPort, listener);
            LinkLog.Info($"Room service listening on port {server.Port}.");
            _ = server.AcceptLoopAsync();
            return server;
        }

        public void UpdateHost(PlayerProfile host)
        {
            _host = host;
            PlayersChanged?.Invoke(this, EventArgs.Empty);
        }

        public IReadOnlyList<PlayerProfile> GetPlayers()
        {
            lock (_guests)
                return new[] { _host }.Concat(_guests.Select(g => g.Profile)).ToList();
        }

        public void RemoveSilentGuests()
        {
            bool changed;
            lock (_guests)
                changed = _guests.RemoveAll(g => DateTime.UtcNow - g.LastSeen > GuestTimeout) > 0;
            if (changed)
                PlayersChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Remove(string machineId)
        {
            lock (_guests)
            {
                _removed[machineId] = DateTime.UtcNow + RemovalTime;
                _guests.RemoveAll(g => g.Profile.MachineId == machineId);
            }
            PlayersChanged?.Invoke(this, EventArgs.Empty);
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { }
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
                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using (client)
            using (var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            using (idle.Token.Register(client.Close))
            {
                try
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    while (true)
                    {
                        idle.CancelAfter(IdleTimeout);
                        var request = await RoomFraming.ReadRequestAsync(stream, idle.Token).ConfigureAwait(false);
                        if (request == null)
                            return;

                        var (status, body) = Handle(request);
                        await RoomFraming.WriteResponseAsync(stream, status, body, idle.Token).ConfigureAwait(false);
                    }
                }
                catch
                {
                }
            }
        }

        private (byte Status, byte[] Body) Handle(RoomFraming.Request request)
        {
            try
            {
                switch (request.Kind)
                {
                    case RoomProtocol.Ping:
                        return (RoomFraming.StatusOk, request.Body);
                    case RoomProtocol.Protocols:
                        return (RoomFraming.StatusOk, _protocolList);
                    case RoomProtocol.ServerPort:
                        return (RoomFraming.StatusOk, new[] { (byte)(_minecraftPort >> 8), (byte)_minecraftPort });
                    case RoomProtocol.PlayerPing:
                        return HandlePlayerPing(request.Body);
                    case RoomProtocol.PlayerList:
                        return (RoomFraming.StatusOk, SerializePlayers());
                    default:
                        return (RoomFraming.StatusError, Encoding.UTF8.GetBytes("Requested protocol hasn't been implemented."));
                }
            }
            catch (Exception ex)
            {
                return (RoomFraming.StatusError, Encoding.UTF8.GetBytes(ex.Message));
            }
        }

        private (byte Status, byte[] Body) HandlePlayerPing(byte[] body)
        {
            var json = Json.Parse(Encoding.UTF8.GetString(body)) as IDictionary<string, object>;
            if (json == null)
                return (RoomFraming.StatusError, Encoding.UTF8.GetBytes("Invalid player profile."));

            var profile = PlayerProfile.FromJson(json, PlayerKind.Guest);
            if (profile == null)
                return (RoomFraming.StatusError, Encoding.UTF8.GetBytes("Invalid player profile."));
            if (profile.MachineId == _host.MachineId)
                return (RoomFraming.StatusError, Encoding.UTF8.GetBytes("The machine id belongs to the host."));
            if (Blocklist.Contains(profile))
                return (RoomFraming.StatusError, Encoding.UTF8.GetBytes(BlockedReply));

            lock (_guests)
            {
                if (_removed.TryGetValue(profile.MachineId, out var until) && until > DateTime.UtcNow)
                    return (RoomFraming.StatusError, Encoding.UTF8.GetBytes(RemovedReply));

                if (IsLocked && !_guests.Any(g => g.Profile.MachineId == profile.MachineId))
                    return (RoomFraming.StatusError, Encoding.UTF8.GetBytes(LockedReply));
            }

            if (PremiumOnly && string.IsNullOrEmpty(profile.Uuid))
                return (RoomFraming.StatusError, Encoding.UTF8.GetBytes(PremiumRequiredReply));

            bool changed;
            lock (_guests)
            {
                var existing = _guests.FirstOrDefault(g => g.Profile.MachineId == profile.MachineId);
                if (existing != null)
                {
                    changed = existing.Profile.Name != profile.Name
                              || existing.Profile.Uuid != profile.Uuid
                              || existing.Profile.GameName != profile.GameName;
                    existing.Profile = profile;
                    existing.LastSeen = DateTime.UtcNow;
                }
                else
                {
                    _guests.Add(new Guest(profile));
                    changed = true;
                }
            }

            if (changed)
                PlayersChanged?.Invoke(this, EventArgs.Empty);

            return (RoomFraming.StatusOk, Array.Empty<byte>());
        }

        private byte[] SerializePlayers()
        {
            var list = GetPlayers().Select(p => p.ToJson(includeKind: true)).ToList();
            return Encoding.UTF8.GetBytes(Json.Serialize(list));
        }

        private sealed class Guest
        {
            public Guest(PlayerProfile profile)
            {
                Profile = profile;
                LastSeen = DateTime.UtcNow;
            }

            public PlayerProfile Profile { get; set; }
            public DateTime LastSeen { get; set; }
        }
    }
}
