// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

using System;
using System.Collections.Generic;
using System.Globalization;
using PCLCS.Link.Minecraft;
using PCLCS.Link.Runtime;

namespace PCLCS.Link.Room
{
    public enum PlayerKind
    {
        Host,
        Guest,
        Local,
    }

    public sealed class PlayerProfile
    {
        public PlayerProfile(string name, string machineId, PlayerKind kind, string? uuid = null, string? gameName = null,
            double? latencyMs = null)
        {
            GameName = string.IsNullOrEmpty(gameName) ? null : gameName;
            Name = name ?? "";
            MachineId = machineId ?? "";
            Kind = kind;
            Uuid = string.IsNullOrWhiteSpace(uuid) ? null : uuid;
            LatencyMs = latencyMs.HasValue && !double.IsNaN(latencyMs.Value) && !double.IsInfinity(latencyMs.Value)
                        && latencyMs.Value >= 0 && latencyMs.Value <= 60000
                ? latencyMs
                : null;
        }

        public string Name { get; }
        public string MachineId { get; }
        public PlayerKind Kind { get; }
        public string? Uuid { get; }
        public string? GameName { get; }
        public double? LatencyMs { get; }

        public static PlayerProfile CreateLocal(PlayerKind kind)
        {
            var account = GameAccount.Current;
            string? uuid = null;
            string? gameName = null;

            if (LinkSettings.ReadGameAccount)
            {
                if (account != null && account.IsPremium && !string.IsNullOrEmpty(account.Uuid))
                {
                    uuid = account.Uuid;
                    gameName = account.Name;
                }
                else if (LinkSettings.LauncherAccountIsPremium && !string.IsNullOrEmpty(LinkSettings.LauncherAccountUuid))
                {
                    uuid = LinkSettings.LauncherAccountUuid;
                    gameName = account?.Name ?? LinkSettings.LauncherAccountName;
                }
            }

            if (gameName == null)
            {
                gameName = account?.Name ?? LinkSettings.LauncherAccountName;
            }

            return new PlayerProfile(LinkSettings.EffectivePlayerName, LinkSettings.MachineId, kind, uuid, gameName);
        }

        public PlayerProfile WithKind(PlayerKind kind)
        {
            return new PlayerProfile(Name, MachineId, kind, Uuid, GameName, LatencyMs);
        }

        public PlayerProfile WithLatency(double? latencyMs)
        {
            return new PlayerProfile(Name, MachineId, Kind, Uuid, GameName, latencyMs);
        }

        public Dictionary<string, object> ToJson(bool includeKind)
        {
            var json = new Dictionary<string, object>
            {
                ["name"] = Name,
                ["machine_id"] = MachineId,
            };
            if (includeKind)
                json["kind"] = Kind == PlayerKind.Host ? "HOST" : "GUEST";
            if (!string.IsNullOrWhiteSpace(Uuid))
                json["uuid"] = Uuid!;
            if (GameName != null)
                json["game_name"] = GameName;
            if (LatencyMs.HasValue)
                json["lat_ms"] = LatencyMs.Value;
            return json;
        }

        public static PlayerProfile FromJson(IDictionary<string, object> json, PlayerKind defaultKind)
        {
            string name = GetString(json, "name");
            string machineId = GetString(json, "machine_id");
            string? rawUuid = json.TryGetValue("uuid", out var u) ? u?.ToString() : null;
            string? uuid = !string.IsNullOrWhiteSpace(rawUuid) ? rawUuid : null;
            string? gameName = json.TryGetValue("game_name", out var g) ? g?.ToString() : null;
            double? latency = json.TryGetValue("lat_ms", out var l) && double.TryParse(l?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                ? lat
                : null;

            PlayerKind kind = defaultKind;
            if (json.TryGetValue("kind", out var k))
                kind = string.Equals(k?.ToString(), "HOST", StringComparison.OrdinalIgnoreCase) ? PlayerKind.Host : PlayerKind.Guest;

            return new PlayerProfile(name, machineId, kind, uuid, gameName, latency);
        }

        private static string GetString(IDictionary<string, object> item, string key)
        {
            return item.TryGetValue(key, out var val) ? val?.ToString() ?? "" : "";
        }
    }
}
