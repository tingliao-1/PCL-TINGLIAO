// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace PCLCS.Link
{
    /// <summary>
    /// Multiplayer preferences and local identity.
    /// </summary>
    public static class LinkSettings
    {
        public static readonly int[] SearchTimeoutChoices = { 30, 60, 120 };

        private static string? _machineId;
        private static readonly ConcurrentDictionary<string, string> Storage = new(StringComparer.OrdinalIgnoreCase);

        static LinkSettings()
        {
            try
            {
                string baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PCL", "Link");
                Directory.CreateDirectory(baseDir);
                DataDirectory = baseDir;
            }
            catch
            {
                DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCL", "Link");
                Directory.CreateDirectory(DataDirectory);
            }
        }

        public static string DataDirectory { get; }

        public static string MachineId
        {
            get
            {
                if (_machineId != null)
                    return _machineId;

                string file = Path.Combine(DataDirectory, "machine_id.txt");
                try
                {
                    if (File.Exists(file))
                    {
                        string id = File.ReadAllText(file).Trim();
                        if (id.Length == 32)
                        {
                            _machineId = id;
                            return id;
                        }
                    }
                    string newId = Guid.NewGuid().ToString("N");
                    File.WriteAllText(file, newId);
                    _machineId = newId;
                    return newId;
                }
                catch
                {
                    _machineId = Guid.NewGuid().ToString("N");
                    return _machineId;
                }
            }
        }

        public static string? LauncherAccountName { get; set; }
        public static string? LauncherAccountUuid { get; set; }
        public static bool LauncherAccountIsPremium { get; set; }

        public static string PlayerName
        {
            get => GetText("link.player", "");
            set => SetText("link.player", value ?? "");
        }

        public static string EffectivePlayerName
        {
            get
            {
                string name = PlayerName.Trim();
                if (name.Length > 0)
                    return name;
                string? game = Minecraft.GameAccount.Current?.Name;
                if (!string.IsNullOrEmpty(game))
                    return game!;
                if (!string.IsNullOrEmpty(LauncherAccountName))
                    return LauncherAccountName!;
                return FallbackPlayerName;
            }
        }

        public static string FallbackPlayerName => "PCL_" + MachineId.Substring(0, 6).ToUpperInvariant();

        public static string LanEntryName
        {
            get => GetText("link.lanname", "");
            set => SetText("link.lanname", value ?? "");
        }

        public static int SearchTimeoutSeconds
        {
            get => int.TryParse(GetText("link.timeout", "60"), NumberStyles.None, CultureInfo.InvariantCulture, out int val) && val > 0 ? val : 60;
            set => SetText("link.timeout", value.ToString(CultureInfo.InvariantCulture));
        }

        public static int LocalPort
        {
            get => int.TryParse(GetText("link.localport", "0"), NumberStyles.None, CultureInfo.InvariantCulture, out int val) ? val : 0;
            set => SetText("link.localport", value.ToString(CultureInfo.InvariantCulture));
        }

        public static bool ShareWithLan
        {
            get => GetBool("link.sharewithlan", false);
            set => SetBool("link.sharewithlan", value);
        }

        public static bool DirectOnly
        {
            get => GetBool("link.directonly", false);
            set => SetBool("link.directonly", value);
        }

        public static bool HideAddress
        {
            get => GetBool("link.hideaddress", false);
            set => SetBool("link.hideaddress", value);
        }

        public static bool AutoOpenLan
        {
            get => GetBool("link.autolan", false);
            set => SetBool("link.autolan", value);
        }

        public static bool ShowSkins
        {
            get => GetBool("link.showskins", true);
            set => SetBool("link.showskins", value);
        }

        public static string ResetMachineId()
        {
            string newId = Guid.NewGuid().ToString("N");
            string file = Path.Combine(DataDirectory, "machine_id.txt");
            try
            {
                File.WriteAllText(file, newId);
            }
            catch { }
            _machineId = newId;
            return newId;
        }

        public static bool ReadGameAccount
        {
            get => GetBool("link.readgameaccount", true);
            set => SetBool("link.readgameaccount", value);
        }

        public static bool ShareMods
        {
            get => GetBool("link.sharemods", false);
            set => SetBool("link.sharemods", value);
        }

        public static bool ServeMods
        {
            get => GetBool("link.servemods", false);
            set => SetBool("link.servemods", value);
        }

        public static string CustomPeers
        {
            get => GetText("link.custompeers", "");
            set => SetText("link.custompeers", value ?? "");
        }

        public static string GetText(string key, string fallback) => Storage.TryGetValue(key, out var val) ? val : fallback;
        public static void SetText(string key, string val) => Storage[key] = val;

        public static bool GetBool(string key, bool fallback) => Storage.TryGetValue(key, out var val) && bool.TryParse(val, out var b) ? b : fallback;
        public static void SetBool(string key, bool val) => Storage[key] = val.ToString();
    }
}
