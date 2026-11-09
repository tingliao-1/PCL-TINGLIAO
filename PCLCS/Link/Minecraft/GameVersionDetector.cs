// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

#nullable enable
using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace PCLCS.Link.Minecraft
{
    /// <summary>
    /// 表示 Minecraft 核心游戏版本与 Mod 加载器信息。
    /// </summary>
    public sealed class GameVersionInfo : IEquatable<GameVersionInfo>
    {
        public string McVersion { get; }
        public string Loader { get; }
        public string? ProtocolVersion { get; }

        public GameVersionInfo(string? mcVersion, string? loader = "Vanilla", string? protocolVersion = null)
        {
            McVersion = string.IsNullOrWhiteSpace(mcVersion) ? "Unknown" : mcVersion!.Trim();
            Loader = string.IsNullOrWhiteSpace(loader) ? "Vanilla" : loader!.Trim();
            ProtocolVersion = string.IsNullOrWhiteSpace(protocolVersion) ? null : protocolVersion!.Trim();
        }

        public string DisplayText
        {
            get
            {
                if (string.Equals(Loader, "Vanilla", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(Loader))
                    return $"Minecraft {McVersion}";
                return $"Minecraft {McVersion} ({Loader})";
            }
        }

        public override string ToString()
        {
            if (string.IsNullOrEmpty(ProtocolVersion))
                return $"{McVersion}|{Loader}";
            return $"{McVersion}|{Loader}|{ProtocolVersion}";
        }

        public static GameVersionInfo? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var parts = text!.Split('|');
            if (parts.Length == 0) return null;
            string mc = parts[0].Trim();
            string loader = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1].Trim() : "Vanilla";
            string? proto = parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]) ? parts[2].Trim() : null;
            return new GameVersionInfo(mc, loader, proto);
        }

        /// <summary>
        /// 判定两个游戏版本是否核心兼容：
        /// 1. Minecraft 核心主次版本必须一致（或其中一个为未知）；
        /// 2. Mod 加载器必须兼容（房主原版时，访客任意加载器均可连接；房主为模组端时，访客必须有兼容的加载器）。
        /// </summary>
        public bool IsCompatibleWith(GameVersionInfo? other)
        {
            if (other == null) return true;
            if (McVersion != "Unknown" && other.McVersion != "Unknown")
            {
                if (!string.Equals(McVersion, other.McVersion, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            string hostLoader = Loader;
            string guestLoader = other.Loader;

            // 房主为原版时，客户端携带任意辅助 Mod（Fabric / Forge / NeoForge / Quilt）只要没有强制服务端 Mod 均可连接
            if (string.Equals(hostLoader, "Vanilla", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrEmpty(hostLoader) || hostLoader == "Unknown")
                return true;

            // 房主为 Fabric
            if (string.Equals(hostLoader, "Fabric", StringComparison.OrdinalIgnoreCase))
                return string.Equals(guestLoader, "Fabric", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(guestLoader, "Quilt", StringComparison.OrdinalIgnoreCase);

            // 房主为 Quilt
            if (string.Equals(hostLoader, "Quilt", StringComparison.OrdinalIgnoreCase))
                return string.Equals(guestLoader, "Quilt", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(guestLoader, "Fabric", StringComparison.OrdinalIgnoreCase);

            // 房主为 Forge
            if (string.Equals(hostLoader, "Forge", StringComparison.OrdinalIgnoreCase))
                return string.Equals(guestLoader, "Forge", StringComparison.OrdinalIgnoreCase);

            // 房主为 NeoForge
            if (string.Equals(hostLoader, "NeoForge", StringComparison.OrdinalIgnoreCase))
                return string.Equals(guestLoader, "NeoForge", StringComparison.OrdinalIgnoreCase);

            return true;
        }

        public bool Equals(GameVersionInfo? other)
        {
            if (other is null) return false;
            return string.Equals(McVersion, other.McVersion, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Loader, other.Loader, StringComparison.OrdinalIgnoreCase);
        }

        public override bool Equals(object? obj) => Equals(obj as GameVersionInfo);

        public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(ToString());
    }

    /// <summary>
    /// 提供实时 Minecraft 游戏核心版本与 Mod 加载器探测服务。
    /// </summary>
    public static class GameVersionDetector
    {
        public static async Task<GameVersionInfo?> DetectAsync(int port)
        {
            string? mcVersion = null;
            string loader = "Vanilla";
            string? protocol = null;

            // 1. 通过 MinecraftProbe legacy ping 探测端口，获取精确的世界核心版本与协议版本号
            try
            {
                var status = await MinecraftProbe.GetStatusAsync(port, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                if (status != null && !string.IsNullOrWhiteSpace(status.Version))
                {
                    mcVersion = status.Version;
                    protocol = status.Protocol;
                }
            }
            catch
            {
            }

            // 2. 查找监听此端口的进程并读取命令行，识别 ModLoader 及在 ping 失败时的版本提取
            try
            {
                int pid = LocalWorldFinder.FindProcess(port);
                if (pid > 0)
                {
                    string? cmdLine = GameAccount.ReadCommandLine(pid);
                    if (!string.IsNullOrEmpty(cmdLine))
                    {
                        loader = DetectLoaderFromCommandLine(cmdLine);

                        if (string.IsNullOrWhiteSpace(mcVersion))
                        {
                            mcVersion = DetectVersionFromCommandLine(cmdLine);
                        }
                    }
                }
            }
            catch
            {
            }

            // 3. Fallback 到当前启动器选中的实例
            if (string.IsNullOrWhiteSpace(mcVersion) || mcVersion == "Unknown")
            {
                if (!string.IsNullOrWhiteSpace(LinkSettings.CurrentInstanceVersion))
                {
                    mcVersion = LinkSettings.CurrentInstanceVersion;
                    if (!string.IsNullOrWhiteSpace(LinkSettings.CurrentInstanceLoader) && loader == "Vanilla")
                    {
                        loader = LinkSettings.CurrentInstanceLoader!;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(mcVersion))
                mcVersion = "Unknown";

            return new GameVersionInfo(mcVersion, loader, protocol);
        }

        public static string DetectLoaderFromCommandLine(string? cmdLine)
        {
            if (string.IsNullOrEmpty(cmdLine)) return "Vanilla";
            if (cmdLine!.IndexOf("net.fabricmc.loader", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cmdLine.IndexOf("fabric-loader", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cmdLine.IndexOf("knot.KnotClient", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Fabric";
            }
            if (cmdLine.IndexOf("net.neoforged", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cmdLine.IndexOf("neoforge", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "NeoForge";
            }
            if (cmdLine.IndexOf("net.minecraftforge", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cmdLine.IndexOf("cpw.mods.modlauncher", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cmdLine.IndexOf("fml.loader", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Forge";
            }
            if (cmdLine.IndexOf("org.quiltmc", StringComparison.OrdinalIgnoreCase) >= 0 ||
                cmdLine.IndexOf("quilt-loader", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "Quilt";
            }
            return "Vanilla";
        }

        public static string? DetectVersionFromCommandLine(string? cmdLine)
        {
            if (string.IsNullOrEmpty(cmdLine)) return null;
            var args = GameAccount.Split(cmdLine!);
            for (int i = 0; i + 1 < args.Count; i++)
            {
                if (args[i] == "--version")
                {
                    string val = args[i + 1];
                    var match = Regex.Match(val, @"\b(1\.\d+(\.\d+)?|\d{2}\.\d+(\.\d+)?)\b");
                    if (match.Success)
                        return match.Value;
                    return val;
                }
            }
            return null;
        }
    }
}
