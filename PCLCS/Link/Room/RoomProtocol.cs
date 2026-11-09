// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

namespace PCLCS.Link.Room
{
    public static class RoomProtocol
    {
        public const string Ping = "c:ping";
        public const string Protocols = "c:protocols";
        public const string ServerPort = "c:server_port";
        public const string PlayerPing = "c:player_ping";
        public const string PlayerList = "c:player_profiles_list";
        public const string AdminAction = "c:admin_action";
        public const string GameVersion = "c:game_version";

        public const string HostNamePrefix = "scaffolding-mc-server-";
        public const string HostAddress = "10.144.144.1";

        public static readonly byte[] Fingerprint =
        {
            0x41, 0x57, 0x48, 0x44, 0x86, 0x37, 0x40, 0x59, 0x57, 0x44, 0x92, 0x43, 0x96, 0x99, 0x85, 0x01,
        };
    }
}
