// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PCLCS.Link.Room;
using PCLCS.Link.Runtime;

namespace PCLCS.Link
{
    public sealed class WhitelistedPlayer
    {
        public WhitelistedPlayer(string name, string? machineId, string? uuid, DateTime time, string? gameName = null)
        {
            Name = name ?? "";
            MachineId = string.IsNullOrWhiteSpace(machineId) ? null : machineId;
            Uuid = string.IsNullOrWhiteSpace(uuid) ? null : uuid;
            GameName = gameName;
            Time = time;
        }

        public string Name { get; set; }
        public string? MachineId { get; set; }
        public string? Uuid { get; set; }
        public string? GameName { get; set; }
        public DateTime Time { get; set; }

        public bool IsBound => !string.IsNullOrEmpty(MachineId) || !string.IsNullOrEmpty(Uuid);

        public bool HasName(string name)
        {
            return string.Equals(name, Name, StringComparison.OrdinalIgnoreCase)
                   || (GameName != null && string.Equals(name, GameName, StringComparison.OrdinalIgnoreCase));
        }
    }

    public static class Whitelist
    {
        private static readonly object Sync = new();
        private static List<WhitelistedPlayer>? _players;

        private static string FilePath => Path.Combine(LinkSettings.DataDirectory, "whitelisted.json");

        public static event EventHandler? Changed;

        public static IReadOnlyList<WhitelistedPlayer> All
        {
            get
            {
                lock (Sync)
                    return Load().ToList();
            }
        }

        public static void Add(PlayerProfile player)
        {
            if (player == null) return;
            lock (Sync)
            {
                var players = Load();
                players.RemoveAll(p => (!string.IsNullOrEmpty(p.MachineId) && p.MachineId == player.MachineId)
                                       || (!string.IsNullOrEmpty(p.Uuid) && player.Uuid != null && string.Equals(p.Uuid, player.Uuid, StringComparison.OrdinalIgnoreCase))
                                       || (string.IsNullOrEmpty(p.MachineId) && string.IsNullOrEmpty(p.Uuid) && p.HasName(player.Name)));
                string? gameName = player.GameName != null && !string.Equals(player.GameName, player.Name, StringComparison.OrdinalIgnoreCase)
                    ? player.GameName
                    : null;
                players.Add(new WhitelistedPlayer(player.Name, player.MachineId, player.Uuid, DateTime.Now, gameName));
                Save(players);
            }
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static void PreAdd(string nameOrUuid)
        {
            if (string.IsNullOrWhiteSpace(nameOrUuid)) return;
            string trimmed = nameOrUuid.Trim();
            bool isUuid = trimmed.Length == 32 || (trimmed.Length == 36 && trimmed.Contains("-"));
            lock (Sync)
            {
                var players = Load();
                if (isUuid)
                {
                    string normUuid = trimmed.Replace("-", "").ToLowerInvariant();
                    if (!players.Any(p => string.Equals(p.Uuid?.Replace("-", ""), normUuid, StringComparison.OrdinalIgnoreCase)))
                    {
                        players.Add(new WhitelistedPlayer(trimmed, null, normUuid, DateTime.Now));
                        Save(players);
                    }
                }
                else
                {
                    if (!players.Any(p => p.HasName(trimmed)))
                    {
                        players.Add(new WhitelistedPlayer(trimmed, null, null, DateTime.Now));
                        Save(players);
                    }
                }
            }
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static void Remove(PlayerProfile player)
        {
            if (player == null) return;
            lock (Sync)
            {
                var players = Load();
                int removed = players.RemoveAll(p => (!string.IsNullOrEmpty(p.MachineId) && p.MachineId == player.MachineId)
                                                    || (!string.IsNullOrEmpty(p.Uuid) && player.Uuid != null && string.Equals(p.Uuid, player.Uuid, StringComparison.OrdinalIgnoreCase))
                                                    || p.HasName(player.Name));
                if (removed > 0)
                    Save(players);
            }
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static void RemoveByIdentifier(string identifier)
        {
            if (string.IsNullOrWhiteSpace(identifier)) return;
            lock (Sync)
            {
                var players = Load();
                int removed = players.RemoveAll(p => p.MachineId == identifier || p.Uuid == identifier || p.HasName(identifier));
                if (removed > 0)
                    Save(players);
            }
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static void ResetBinding(string nameOrId)
        {
            if (string.IsNullOrWhiteSpace(nameOrId)) return;
            lock (Sync)
            {
                var players = Load();
                var target = players.FirstOrDefault(p => p.HasName(nameOrId) || p.MachineId == nameOrId);
                if (target != null)
                {
                    target.MachineId = null;
                    Save(players);
                }
            }
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static void Clear()
        {
            lock (Sync)
            {
                var players = Load();
                players.Clear();
                Save(players);
            }
            Changed?.Invoke(null, EventArgs.Empty);
        }

        public static bool Contains(PlayerProfile player)
        {
            if (player == null) return false;
            var (isAllowed, _) = CheckAccess(player);
            return isAllowed;
        }

        public static (bool IsAllowed, string Reason) CheckAccess(PlayerProfile incoming)
        {
            if (incoming == null) return (false, "Null_Profile");
            lock (Sync)
            {
                var players = Load();

                // 1. Match by Microsoft UUID (Strongest, invariant across devices and name changes)
                if (!string.IsNullOrEmpty(incoming.Uuid))
                {
                    string normUuid = incoming.Uuid!.Replace("-", "").ToLowerInvariant();
                    var matchByUuid = players.FirstOrDefault(p => !string.IsNullOrEmpty(p.Uuid) &&
                        string.Equals(p.Uuid!.Replace("-", ""), normUuid, StringComparison.OrdinalIgnoreCase));
                    if (matchByUuid != null)
                    {
                        if (matchByUuid.Name != incoming.Name)
                        {
                            matchByUuid.Name = incoming.Name;
                            Save(players);
                        }
                        return (true, "Match_UUID");
                    }
                }

                // 2. Match by Bound MachineId (Device fingerprint match)
                if (!string.IsNullOrEmpty(incoming.MachineId))
                {
                    var matchByMachine = players.FirstOrDefault(p => !string.IsNullOrEmpty(p.MachineId) && p.MachineId == incoming.MachineId);
                    if (matchByMachine != null)
                    {
                        return (true, "Match_MachineId");
                    }
                }

                // 3. Match by Name (TOFU - Trust On First Use auto binding)
                var matchByName = players.FirstOrDefault(p => p.HasName(incoming.Name));
                if (matchByName != null)
                {
                    // If this record has already bound to a different machine -> reject impersonation!
                    if (!string.IsNullOrEmpty(matchByName.MachineId) && matchByName.MachineId != incoming.MachineId)
                    {
                        return (false, "Device_Mismatch_Impersonation");
                    }

                    // TOFU: Bind the incoming machine ID on first arrival
                    if (string.IsNullOrEmpty(matchByName.MachineId) && !string.IsNullOrEmpty(incoming.MachineId))
                    {
                        matchByName.MachineId = incoming.MachineId;
                        if (string.IsNullOrEmpty(matchByName.Uuid) && !string.IsNullOrEmpty(incoming.Uuid))
                            matchByName.Uuid = incoming.Uuid;
                        Save(players);
                        LinkLog.Info($"[Whitelist TOFU] Auto-bound MachineId for '{incoming.Name}' to {incoming.MachineId}.");
                    }
                    return (true, "TOFU_Bound_Success");
                }

                return (false, "Not_In_Whitelist");
            }
        }

        private static List<WhitelistedPlayer> Load()
        {
            if (_players != null)
                return _players;

            try
            {
                string? text = AtomicFile.ReadAllTextWithBackup(FilePath);
                if (!string.IsNullOrEmpty(text) && Json.TryParse(text) is object[] items)
                {
                    var list = new List<WhitelistedPlayer>();
                    foreach (var item in items.OfType<Dictionary<string, object>>())
                    {
                        string name = item.TryGetValue("name", out var n) ? n?.ToString() ?? "" : "";
                        string? machineId = item.TryGetValue("machine_id", out var m) ? m?.ToString() : null;
                        if (string.IsNullOrWhiteSpace(machineId)) machineId = null;
                        string? uuid = item.TryGetValue("uuid", out var u) ? u?.ToString() : null;
                        if (string.IsNullOrWhiteSpace(uuid)) uuid = null;
                        string? gameName = item.TryGetValue("game_name", out var g) ? g?.ToString() : null;
                        list.Add(new WhitelistedPlayer(name, machineId, uuid, DateTime.Now, gameName));
                    }
                    _players = list;
                    return list;
                }
            }
            catch
            {
            }

            _players = new List<WhitelistedPlayer>();
            return _players;
        }

        private static void Save(List<WhitelistedPlayer> players)
        {
            _players = players;
            try
            {
                var list = new List<Dictionary<string, object>>();
                foreach (var p in players)
                {
                    var dict = new Dictionary<string, object>
                    {
                        ["name"] = p.Name,
                    };
                    if (p.MachineId != null) dict["machine_id"] = p.MachineId;
                    if (p.Uuid != null) dict["uuid"] = p.Uuid;
                    if (p.GameName != null) dict["game_name"] = p.GameName;
                    list.Add(dict);
                }
                AtomicFile.WriteAllTextAtomic(FilePath, Json.Serialize(list));
            }
            catch
            {
            }
        }
    }
}
