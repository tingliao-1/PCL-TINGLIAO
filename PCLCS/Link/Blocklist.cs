// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PCLCS.Link.Room;

namespace PCLCS.Link
{
    public sealed class BlockedPlayer
    {
        public BlockedPlayer(string machineId, string name, string? gameName, string? uuid, DateTime time)
        {
            MachineId = machineId ?? "";
            Name = name ?? "";
            GameName = gameName;
            Uuid = uuid;
            Time = time;
        }

        public string MachineId { get; }
        public string Name { get; }
        public string? GameName { get; }
        public string? Uuid { get; }
        public DateTime Time { get; }

        public bool HasName(string name)
        {
            return string.Equals(name, Name, StringComparison.OrdinalIgnoreCase)
                   || (GameName != null && string.Equals(name, GameName, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static class Blocklist
    {
        private static readonly object Sync = new();
        private static List<BlockedPlayer>? _players;

        private static string FilePath => Path.Combine(LinkSettings.DataDirectory, "blocked.json");

        public static event EventHandler? Changed;

        public static IReadOnlyList<BlockedPlayer> All
        {
            get
            {
                lock (Sync)
                    return Load().ToList();
            }
        }

        public static void Add(PlayerProfile player)
        {
            lock (Sync)
            {
                var players = Load();
                players.RemoveAll(p => p.MachineId == player.MachineId);
                string? gameName = player.GameName != null && !string.Equals(player.GameName, player.Name, StringComparison.OrdinalIgnoreCase)
                    ? player.GameName
                    : null;
                players.Add(new BlockedPlayer(player.MachineId, player.Name, gameName, player.Uuid, DateTime.Now));
                Save(players);
            }
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static void Remove(string machineId)
        {
            lock (Sync)
            {
                var players = Load();
                if (players.RemoveAll(p => p.MachineId == machineId) == 0)
                    return;
                Save(players);
            }
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static bool Contains(PlayerProfile player)
        {
            lock (Sync)
            {
                return Load().Any(p => p.MachineId == player.MachineId
                                       || (p.Uuid != null && string.Equals(p.Uuid, player.Uuid, StringComparison.OrdinalIgnoreCase)));
            }
        }

        private static List<BlockedPlayer> Load()
        {
            if (_players != null)
                return _players;

            try
            {
                if (File.Exists(FilePath))
                {
                    string text = File.ReadAllText(FilePath);
                    if (Json.TryParse(text) is object[] items)
                    {
                        var list = new List<BlockedPlayer>();
                        foreach (var item in items.OfType<Dictionary<string, object>>())
                        {
                            string machineId = item.TryGetValue("machine_id", out var m) ? m?.ToString() ?? "" : "";
                            string name = item.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";
                            string? gameName = item.TryGetValue("game_name", out var g) ? g?.ToString() : null;
                            string? uuid = item.TryGetValue("uuid", out var u) ? u?.ToString() : null;
                            list.Add(new BlockedPlayer(machineId, name, gameName, uuid, DateTime.Now));
                        }
                        _players = list;
                        return list;
                    }
                }
            }
            catch
            {
            }

            _players = new List<BlockedPlayer>();
            return _players;
        }

        private static void Save(List<BlockedPlayer> players)
        {
            _players = players;
            try
            {
                var list = new List<Dictionary<string, object>>();
                foreach (var p in players)
                {
                    var dict = new Dictionary<string, object>
                    {
                        ["machine_id"] = p.MachineId,
                        ["name"] = p.Name,
                    };
                    if (p.GameName != null) dict["game_name"] = p.GameName;
                    if (p.Uuid != null) dict["uuid"] = p.Uuid;
                    list.Add(dict);
                }
                File.WriteAllText(FilePath, Json.Serialize(list));
            }
            catch
            {
            }
        }
    }
}
