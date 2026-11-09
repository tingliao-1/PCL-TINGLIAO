// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using PCLCS.Link.Contracts;
using PCLCS.Link.Minecraft;
using PCLCS.Link.Room;
using PCLCS.Link.Runtime;

namespace PCLCS.Link
{
    public enum LinkRole
    {
        None,
        Host,
        Guest,
    }

    public enum LinkStage
    {
        Idle,
        Working,
        Ready,
        Failed,
    }

    public enum LinkStep
    {
        Component,
        World,
        Network,
        FindHost,
        Enter,
    }

    public enum StepState
    {
        Pending,
        Active,
        Done,
    }

    public sealed class LinkSession
    {
        private static readonly TimeSpan DirectGracePeriod = TimeSpan.FromSeconds(8);
        private static readonly TimeSpan ConnectRoomTimeout = TimeSpan.FromSeconds(30);

        private readonly SynchronizationContext? _ui;
        private readonly Dictionary<LinkStep, StepState> _steps = new();

        private CancellationTokenSource? _run;
        private EasyTierInstance? _network;
        private RoomServer? _server;
        private RoomClient? _client;
        private LanWorldAnnouncer? _announcer;
        private GameProxy? _gate;
        private PlayerProfile? _self;
        private int _serviceLocalPort;

        public LinkSession(SynchronizationContext? ui = null)
        {
            _ui = ui ?? SynchronizationContext.Current;
        }

        public event EventHandler? Changed;

        public LinkRole Role { get; private set; }
        public LinkStage Stage { get; private set; }
        public bool IsActive => Stage == LinkStage.Working || Stage == LinkStage.Ready;

        public IReadOnlyList<LinkStep> Steps => Role == LinkRole.Host
            ? new[] { LinkStep.Component, LinkStep.World, LinkStep.Network }
            : new[] { LinkStep.Component, LinkStep.Network, LinkStep.FindHost, LinkStep.Enter };

        public DownloadProgress? Download { get; private set; }
        public RoomCode? Code { get; private set; }
        public int MinecraftPort { get; private set; }
        public int LocalPort { get; private set; }
        public bool IsSharedWithLan { get; private set; }
        public IReadOnlyList<PlayerProfile> Players { get; private set; } = Array.Empty<PlayerProfile>();
        public EasyTierPeer? HostPeer { get; private set; }
        public int DirectPeers { get; private set; }
        public int RelayedPeers { get; private set; }
        public int ConnectedNodes { get; private set; } = -1;
        public NatKind LocalNat { get; private set; }
        public TimeSpan SearchTime { get; private set; }
        public LinkException? Error { get; private set; }
        public GameVersionInfo? HostGameVersion { get; set; }

        public LinkRoomSnapshotDto GetRoomSnapshot(bool isServerSide)
        {
            string pingText;
            string pingToolTip;
            if (isServerSide)
            {
                pingText = $"{ConnectedNodes} 节点";
                pingToolTip = $"已连接公共中继节点：{ConnectedNodes} 个";
            }
            else
            {
                if (HostPeer?.LatencyMs.HasValue == true)
                    pingText = $"{HostPeer.LatencyMs.Value:0} ms";
                else
                    pingText = "连接中";
                pingToolTip = HostPeer?.IsDirect == true ? "与房主 P2P 直连" : "通过中继节点转发";
            }

            int count = Players?.Count ?? 0;
            string playerCountSummary = $"{Math.Max(1, count)} 人";
            string playerCountDetail = $"{count} 人在线";

            string hintText;
            int hintTheme; // 0 = Blue, 2 = Yellow
            if (isServerSide)
            {
                if (DirectPeers > 0)
                {
                    hintText = $"已与 {DirectPeers} 位访客建立 P2P 直连通道，延迟极低。";
                    hintTheme = 0; // Blue
                }
                else if (RelayedPeers > 0)
                {
                    hintText = "当前有访客通过中继节点传输，可在设置中切换或添加优质节点。";
                    hintTheme = 2; // Yellow
                }
                else
                {
                    hintText = "房间已准备就绪，正在等待好友输入邀请码加入……";
                    hintTheme = 0; // Blue
                }
            }
            else
            {
                if (HostPeer?.IsDirect == true)
                {
                    hintText = "已与房主成功建立 P2P 直连通道，延迟接近局域网！";
                    hintTheme = 0; // Blue
                }
                else
                {
                    hintText = "双方网络受限未能直连，当前正在通过公共中继节点进行加密转发。";
                    hintTheme = 2; // Yellow
                }
            }

            bool isLocalAdmin = !isServerSide && (IsLocalAdmin || _self?.Kind == PlayerKind.Admin);
            var playerList = Players;
            var viewDtos = new List<LinkPlayerViewDto>(playerList?.Count ?? 0);
            if (playerList != null)
            {
                for (int i = 0; i < playerList.Count; i++)
                {
                    viewDtos.Add(new LinkPlayerViewDto(playerList[i], isServerSide, isLocalAdmin));
                }
            }

            return new LinkRoomSnapshotDto(
                isServerSide,
                pingText,
                pingToolTip,
                playerCountSummary,
                playerCountDetail,
                hintText,
                hintTheme,
                viewDtos,
                HostGameVersion
            );
        }

        public StepState GetStepState(LinkStep step)
        {
            return _steps.TryGetValue(step, out var state) ? state : StepState.Pending;
        }

        // ======================== Host ========================

        public async void StartHost(int? manualPort)
        {
            var run = Begin(LinkRole.Host);
            var token = run.Token;
            try
            {
                EnsureSupportedSystem();

                SetStep(LinkStep.World, StepState.Active);
                var world = manualPort.HasValue
                    ? Task.FromResult(manualPort.Value)
                    : LocalWorldFinder.WaitForLocalWorldAsync(token);

                await InstallComponentAsync(token);

                int port = await world;
                if (manualPort.HasValue && !await MinecraftProbe.IsServerAsync(port, TimeSpan.FromSeconds(3)))
                    throw new LinkException("Link.Error.PortNotGame", port);
                token.ThrowIfCancellationRequested();
                MinecraftPort = port;
                SetStep(LinkStep.World, StepState.Done);
                LinkLog.Info($"Hosting the LAN world on port {port}.");

                SetStep(LinkStep.Network, StepState.Active);
                Code = RoomCode.Create();
                await GameAccount.RefreshAsync();
                _self = PlayerProfile.CreateLocal(PlayerKind.Host);

                try
                {
                    HostGameVersion = await GameVersionDetector.DetectAsync(port).ConfigureAwait(false);
                    LinkLog.Info($"Host game version detected: {HostGameVersion?.DisplayText ?? "Unknown"}");
                }
                catch (Exception ex)
                {
                    LinkLog.Warn($"Failed to detect host game version: {ex.Message}");
                }

                _gate = GameProxy.Start(new IPEndPoint(IPAddress.Loopback, 0), port);
                _server = RoomServer.Start(_self, _gate.Port);
                _server.GameVersion = HostGameVersion;
                _server.PlayersChanged += (sender, e) => Post(run, () =>
                {
                    Players = _server?.GetPlayers() ?? Players;
                    OnChanged();
                });

                var config = CreateConfig(Code, RoomProtocol.HostNamePrefix + _server.Port, RoomProtocol.HostAddress);
                config.TcpWhitelist.Add(_server.Port.ToString());
                config.TcpWhitelist.Add(_gate.Port.ToString());
                config.UdpWhitelist.Add(_gate.Port.ToString());
                await StartNetworkAsync(config, run);

                for (int i = 0; i < 10 && ConnectedNodes <= 0; i++)
                {
                    ConnectedNodes = await _network!.CountConnectedNodesAsync(token);
                    if (ConnectedNodes <= 0)
                        await Task.Delay(1000, token);
                }

                Players = _server.GetPlayers();
                SetStep(LinkStep.Network, StepState.Done);
                Stage = LinkStage.Ready;
                LinkLog.Info($"Room {Mask(Code)} is open ({ConnectedNodes} nodes connected).");
                OnChanged();

                await MonitorHostAsync(run);
            }
            catch (Exception ex)
            {
                HandleFailure(run, ex);
            }
        }

        private async Task MonitorHostAsync(CancellationTokenSource run)
        {
            var token = run.Token;
            int tick = 0;
            int missedPings = 0;
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(2000, token);
                tick++;

                _server?.RemoveSilentGuests();
                if (_network != null)
                {
                    var peers = await _network.GetPeersAsync(token);
                    var members = peers.Where(p => p.IsMember && !p.IsLocal).ToList();
                    DirectPeers = members.Count(p => p.IsDirect);
                    RelayedPeers = members.Count - DirectPeers;
                    LocalNat = peers.FirstOrDefault(p => p.IsLocal)?.Nat ?? LocalNat;
                }

                if (tick % 3 == 0)
                {
                    if (_network != null)
                        ConnectedNodes = await _network.CountConnectedNodesAsync(token);

                    if (await MinecraftProbe.IsServerAsync(MinecraftPort, TimeSpan.FromSeconds(4)))
                        missedPings = 0;
                    else if (++missedPings >= 3)
                        throw new LinkException("Link.Error.WorldClosed");
                }
                OnChanged();
            }
        }

        // ======================== Guest ========================

        public async void StartJoin(RoomCode code)
        {
            var run = Begin(LinkRole.Guest);
            var token = run.Token;
            Code = code;
            try
            {
                EnsureSupportedSystem();
                await InstallComponentAsync(token);

                SetStep(LinkStep.Network, StepState.Active);
                await GameAccount.RefreshAsync();
                _self = PlayerProfile.CreateLocal(PlayerKind.Local);

                var config = CreateConfig(code, "pcl-" + Hex.Random(4), null);
                config.TcpWhitelist.Add("0");
                config.UdpWhitelist.Add("0");
                await StartNetworkAsync(config, run);
                SetStep(LinkStep.Network, StepState.Done);

                SetStep(LinkStep.FindHost, StepState.Active);
                var host = await FindHostAsync(config.DirectOnly, config.HideAddress, token);
                var hostAddress = IPAddress.Parse(host.IPv4);
                int servicePort = int.Parse(host.Hostname.Substring(RoomProtocol.HostNamePrefix.Length));
                SetStep(LinkStep.FindHost, StepState.Done);

                SetStep(LinkStep.Enter, StepState.Active);
                _serviceLocalPort = EasyTierInstance.FindFreePort();
                if (!await _network!.AddPortForwardAsync("tcp", new IPEndPoint(IPAddress.Loopback, _serviceLocalPort),
                        new IPEndPoint(hostAddress, servicePort), token))
                    throw new LinkException("Link.Error.ForwardFailed");

                _client = await ConnectRoomAsync(_serviceLocalPort, token);
                int hostPort = await _client.GetServerPortAsync(token);

                try
                {
                    HostGameVersion = await _client.GetGameVersionAsync(token).ConfigureAwait(false);
                    LinkLog.Info($"Host game version received from room: {HostGameVersion?.DisplayText ?? "Unknown"}");
                }
                catch (Exception ex)
                {
                    LinkLog.Warn($"Failed to query host game version: {ex.Message}");
                }

                IsSharedWithLan = LinkSettings.ShareWithLan;
                var bindAddress = IsSharedWithLan ? IPAddress.Any : IPAddress.Loopback;
                int localPort = ChooseLocalPort(hostPort, bindAddress);
                var target = new IPEndPoint(hostAddress, hostPort);

                if (!await _network.AddPortForwardAsync("tcp", new IPEndPoint(bindAddress, localPort), target, token)
                    || !await _network.AddPortForwardAsync("udp", new IPEndPoint(bindAddress, localPort), target, token))
                    throw new LinkException("Link.Error.ForwardFailed");
                if (IsSharedWithLan)
                    await _network.AddPortForwardAsync("tcp", new IPEndPoint(IPAddress.IPv6Any, localPort), target, token);
                LocalPort = localPort;
                LinkLog.Info($"Host game port {hostPort} reachable at local port {localPort}.");

                _self = _self.WithLatency(HostPeer?.LatencyMs);
                await _client.SendHeartbeatAsync(_self, token);
                Players = MarkLocal(await _client.GetPlayersAsync(token));

                string motd = LinkSettings.LanEntryName;
                if (string.IsNullOrWhiteSpace(motd))
                    motd = $"{HostPeer?.Hostname ?? "PCL Room"} (PCL2)";
                _announcer = new LanWorldAnnouncer(() => motd, localPort, !IsSharedWithLan);

                SetStep(LinkStep.Enter, StepState.Done);
                Stage = LinkStage.Ready;
                LinkLog.Info($"Joined room {Mask(Code)}.");
                OnChanged();

                await MonitorGuestAsync(run);
            }
            catch (Exception ex)
            {
                HandleFailure(run, ex);
            }
        }

        private async Task<EasyTierPeer> FindHostAsync(bool directOnly, bool hideAddress, CancellationToken token)
        {
            var started = DateTime.UtcNow;
            DateTime? firstSeen = null;
            while (true)
            {
                SearchTime = DateTime.UtcNow - started;
                var peers = await _network!.GetPeersAsync(token);
                LocalNat = peers.FirstOrDefault(p => p.IsLocal)?.Nat ?? LocalNat;

                var host = peers.FirstOrDefault(IsRoomHost);
                if (host != null)
                {
                    HostPeer = host;
                    firstSeen ??= DateTime.UtcNow;
                    if (host.IsDirect || hideAddress || (!directOnly && DateTime.UtcNow - firstSeen > DirectGracePeriod))
                    {
                        LinkLog.Info($"Found host {host.Hostname} ({(host.IsDirect ? "direct" : "relayed")}, {host.Tunnel}).");
                        return host;
                    }
                }

                if (SearchTime.TotalSeconds > LinkSettings.SearchTimeoutSeconds)
                {
                    if (host != null)
                        throw new LinkException("Link.Error.NoDirectPath");
                    if (ConnectedNodes == 0)
                        throw new LinkException("Link.Error.NoNodes");
                    throw new LinkException("Link.Error.RoomNotFound");
                }

                if (SearchTime.TotalSeconds > 10 && (int)SearchTime.TotalSeconds % 5 < 2)
                    ConnectedNodes = await _network.CountConnectedNodesAsync(token);
                OnChanged();
                await Task.Delay(1500, token);
            }
        }

        private static bool IsRoomHost(EasyTierPeer peer)
        {
            return !peer.IsLocal && peer.IsMember
                && peer.Hostname.StartsWith(RoomProtocol.HostNamePrefix, StringComparison.Ordinal)
                && int.TryParse(peer.Hostname.Substring(RoomProtocol.HostNamePrefix.Length), out int port) && port > 0 && port < 65536;
        }

        private static async Task<RoomClient> ConnectRoomAsync(int localPort, CancellationToken token)
        {
            var deadline = DateTime.UtcNow + ConnectRoomTimeout;
            while (DateTime.UtcNow < deadline)
            {
                var client = await RoomClient.TryConnectAsync(localPort, token);
                if (client != null)
                    return client;
                await Task.Delay(2000, token);
            }
            throw new LinkException("Link.Error.HostNoAnswer");
        }

        private async Task MonitorGuestAsync(CancellationTokenSource run)
        {
            var token = run.Token;
            int tick = 0;
            int failures = 0;
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(2000, token);
                tick++;

                var peers = await _network!.GetPeersAsync(token);
                LocalNat = peers.FirstOrDefault(p => p.IsLocal)?.Nat ?? LocalNat;
                var host = peers.FirstOrDefault(IsRoomHost);
                if (host != null)
                    HostPeer = host;

                if (tick % 2 == 0 && _client != null && _self != null)
                {
                    try
                    {
                        _self = _self.WithLatency(HostPeer?.LatencyMs);
                        await _client.SendHeartbeatAsync(_self, token);
                        bool isNowAdmin = _client.IsAdmin;
                        if ((_self.Kind == PlayerKind.Admin) != isNowAdmin)
                        {
                            _self = _self.WithKind(isNowAdmin ? PlayerKind.Admin : PlayerKind.Guest);
                        }
                        Players = MarkLocal(await _client.GetPlayersAsync(token));
                        if (HostGameVersion == null)
                        {
                            try
                            {
                                HostGameVersion = await _client.GetGameVersionAsync(token).ConfigureAwait(false);
                            }
                            catch
                            {
                            }
                        }
                        failures = 0;
                    }
                    catch (Exception ex) when (RoomClient.IsConnectionError(ex) && !token.IsCancellationRequested)
                    {
                        LinkLog.Warn("Heartbeat failed: " + ex.Message);
                        if (++failures >= 3)
                            throw new LinkException("Link.Error.HostNoAnswer");
                    }
                }

                OnChanged();
            }
        }

        public bool IsLocked
        {
            get => _server?.IsLocked ?? false;
            set { if (_server != null) _server.IsLocked = value; }
        }

        public bool PremiumOnly
        {
            get => _server?.PremiumOnly ?? false;
            set { if (_server != null) _server.PremiumOnly = value; }
        }

        public bool WhitelistOnly
        {
            get => _server?.WhitelistOnly ?? false;
            set { if (_server != null) _server.WhitelistOnly = value; }
        }

        public void AddWhitelist(PlayerProfile player)
        {
            Whitelist.Add(player);
            OnChanged();
        }

        public void RemoveWhitelist(PlayerProfile player)
        {
            Whitelist.Remove(player);
            OnChanged();
        }

        public bool IsWhitelisted(PlayerProfile player)
        {
            return Whitelist.Contains(player);
        }

        public void ResetWhitelistBinding(PlayerProfile player)
        {
            if (player == null) return;
            Whitelist.ResetBinding(player.Name);
            OnChanged();
        }

        public void EnforceSecurityPolicies(bool evictNonPremium = true)
        {
            if (Role == LinkRole.Host && _server != null)
            {
                _server.EnforceSecurityPolicies(evictNonPremium);
            }
        }

        public bool IsLocalAdmin => _client?.IsAdmin ?? false;
        public string? AdminToken => _client?.AdminToken;

        public void SetPlayerAdmin(string machineId, bool isAdmin)
        {
            if (Role != LinkRole.Host || _server == null)
                return;

            _server.SetAdmin(machineId, isAdmin);
            Players = _server.GetPlayers();
            OnChanged();
        }

        public async Task KickPlayerAsync(PlayerProfile player)
        {
            if (player == null) return;

            if (Role == LinkRole.Host && _server != null)
            {
                RemovePlayer(player, block: false);
            }
            else if (Role == LinkRole.Guest && _client != null && IsLocalAdmin && !string.IsNullOrEmpty(AdminToken))
            {
                await _client.SendAdminKickAsync(AdminToken!, player.MachineId, CancellationToken.None);
                Players = MarkLocal(await _client.GetPlayersAsync(CancellationToken.None));
                OnChanged();
            }
        }

        public void RemovePlayer(PlayerProfile player, bool block)
        {
            if (Role != LinkRole.Host || _server == null || player.Kind == PlayerKind.Host)
                return;

            if (block)
                Blocklist.Add(player);

            _server.Remove(player.MachineId);
            Players = _server.GetPlayers();
            OnChanged();
        }

        public void Stop()
        {
            if (Stage == LinkStage.Idle)
                return;
            LinkLog.Info("Room stopped by the user.");
            Release();
            Role = LinkRole.None;
            Stage = LinkStage.Idle;
            Error = null;
            OnChanged();
        }

        public void Shutdown()
        {
            Release();
        }

        // ======================== Helpers ========================

        private CancellationTokenSource Begin(LinkRole role)
        {
            Release();
            _run = new CancellationTokenSource();
            _steps.Clear();
            Role = role;
            Stage = LinkStage.Working;
            Error = null;
            Download = null;
            Code = null;
            MinecraftPort = 0;
            LocalPort = 0;
            IsSharedWithLan = false;
            Players = Array.Empty<PlayerProfile>();
            HostPeer = null;
            DirectPeers = 0;
            RelayedPeers = 0;
            ConnectedNodes = -1;
            LocalNat = NatKind.Unknown;
            SearchTime = TimeSpan.Zero;
            _serviceLocalPort = 0;
            OnChanged();
            return _run;
        }

        private void Release()
        {
            _run?.Cancel();
            _run = null;
            _announcer?.Dispose();
            _announcer = null;
            _gate?.Dispose();
            _gate = null;
            _client?.Dispose();
            _client = null;
            _server?.Dispose();
            _server = null;
            _network?.Dispose();
            _network = null;
        }

        private void HandleFailure(CancellationTokenSource run, Exception ex)
        {
            if (run != _run || run.IsCancellationRequested)
                return;

            Error = ex as LinkException ?? new LinkException(ex, "Link.Error.Unexpected", ex.Message);
            LinkLog.Error("Room failed: " + Error.Message, ex is LinkException ? ex.InnerException : ex);
            Release();
            Stage = LinkStage.Failed;
            OnChanged();
        }

        private async Task InstallComponentAsync(CancellationToken token)
        {
            SetStep(LinkStep.Component, StepState.Active);
            var progress = new Progress<DownloadProgress>(p =>
            {
                Download = p;
                OnChanged();
            });
            await EasyTierPackage.InstallAsync(progress, token);
            Download = null;
            SetStep(LinkStep.Component, StepState.Done);
        }

        private async Task StartNetworkAsync(EasyTierConfig config, CancellationTokenSource run)
        {
            var token = run.Token;
            _network = await Task.Run(() => EasyTierInstance.Start(config), token);
            _network.Exited += (sender, e) => Post(run, () =>
            {
                if (Stage != LinkStage.Idle)
                    HandleFailure(run, new LinkException("Link.Error.ProcessExited"));
            });
        }

        private static EasyTierConfig CreateConfig(RoomCode code, string hostname, string? ipv4)
        {
            var config = new EasyTierConfig
            {
                NetworkName = code.NetworkName,
                NetworkSecret = code.NetworkSecret,
                Hostname = hostname,
                IPv4 = ipv4,
                DirectOnly = LinkSettings.DirectOnly,
                HideAddress = LinkSettings.HideAddress,
            };

            var peers = new List<string>();
            string custom = LinkSettings.CustomPeers;
            if (!string.IsNullOrWhiteSpace(custom))
            {
                peers.AddRange(custom.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()));
            }
            if (peers.Count == 0)
            {
                peers.AddRange(PublicNodes.All.Select(n => n.Url));
            }

            config.Peers.AddRange(peers);
            return config;
        }

        private static int ChooseLocalPort(int hostPort, IPAddress bind)
        {
            int configured = LinkSettings.LocalPort;
            if (configured > 0 && IsPortFree(bind, configured))
                return configured;
            if (IsPortFree(bind, hostPort))
                return hostPort;
            return EasyTierInstance.FindFreePort();
        }

        private static bool IsPortFree(IPAddress bind, int port)
        {
            try
            {
                var listener = new TcpListener(bind, port);
                listener.Start();
                listener.Stop();
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private IReadOnlyList<PlayerProfile> MarkLocal(IReadOnlyList<PlayerProfile> players)
        {
            string? myMachineId = _self?.MachineId;
            return players.Select(p => p.MachineId == myMachineId ? p.WithLocal(true) : p).ToList();
        }

        private void SetStep(LinkStep step, StepState state)
        {
            _steps[step] = state;
            OnChanged();
        }

        private static void EnsureSupportedSystem()
        {
            if (!EasyTierPackage.IsSupportedSystem)
                throw new LinkException("Link.Error.UnsupportedSystem");
        }

        private static string Mask(RoomCode? code)
        {
            return code != null ? code.Code.Substring(0, 10) + "..." : "";
        }

        private void Post(CancellationTokenSource run, Action action)
        {
            if (_ui != null)
                _ui.Post(_ => { if (run == _run) action(); }, null);
            else if (run == _run)
                action();
        }

        private void OnChanged()
        {
            if (_ui != null)
                _ui.Post(_ => Changed?.Invoke(this, EventArgs.Empty), null);
            else
                Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
