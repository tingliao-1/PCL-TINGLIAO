// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

#nullable enable
using System;
using System.Text;
using PCLCS.Link.Room;

namespace PCLCS.Link.Contracts
{
    /// <summary>
    /// Strongly-typed presentation DTO for displaying a player in the link room UI.
    /// Pre-computes all formatting, display tags, and tooltips in C# to ensure zero late-binding in VB.NET.
    /// </summary>
    public sealed class LinkPlayerViewDto
    {
        public string MachineId { get; }
        public string DisplayName { get; }
        public string? SubName { get; }
        public bool IsHost { get; }
        public bool IsAdmin { get; }
        public bool IsLocalMe { get; }
        public bool IsOfflineMode { get; }
        public string PingText { get; }
        public string StatusBrushKey { get; }
        public string TooltipText { get; }

        public string RoleBadgeText { get; }
        public string RoleBadgeFgBrushKey { get; }
        public string RoleBadgeBgBrushKey { get; }

        public bool CanManage { get; }
        public bool CanKick { get; }
        public bool CanBlock { get; }
        public bool CanSetAdmin { get; }
        public bool CanRevokeAdmin { get; }
        public bool IsWhitelisted { get; }
        public bool CanToggleWhitelist { get; }
        public bool CanResetDeviceBinding { get; }

        public PlayerProfile RawProfile { get; }

        public LinkPlayerViewDto(PlayerProfile profile, bool isServerSide, bool isLocalAdmin = false)
        {
            RawProfile = profile ?? throw new ArgumentNullException(nameof(profile));
            MachineId = profile.MachineId;
            DisplayName = profile.Name;
            SubName = (!string.IsNullOrEmpty(profile.GameName) && profile.GameName != profile.Name)
                ? $"[{profile.GameName}]"
                : null;

            IsHost = profile.Kind == PlayerKind.Host;
            IsAdmin = profile.Kind == PlayerKind.Admin;
            IsLocalMe = profile.IsLocal || profile.Kind == PlayerKind.Local;
            IsOfflineMode = string.IsNullOrEmpty(profile.Uuid);
            IsWhitelisted = Whitelist.Contains(profile);
            CanToggleWhitelist = isServerSide && !IsHost;
            CanResetDeviceBinding = isServerSide && !IsHost && IsWhitelisted && IsOfflineMode;

            if (profile.LatencyMs.HasValue)
                PingText = $"{profile.LatencyMs.Value:0} ms";
            else if (IsHost)
                PingText = isServerSide ? "本地" : "房主";
            else
                PingText = "-";

            StatusBrushKey = IsHost ? "ColorBrush3" : (profile.LatencyMs.HasValue ? "ColorBrush2" : "ColorBrushGray3");

            // Role badge text and color palette
            if (IsHost)
            {
                RoleBadgeText = "房主";
                RoleBadgeFgBrushKey = "ColorBrush2";
                RoleBadgeBgBrushKey = "ColorBrush7";
            }
            else if (IsAdmin)
            {
                RoleBadgeText = "管理";
                RoleBadgeFgBrushKey = "ColorBrush3";
                RoleBadgeBgBrushKey = "ColorBrush7";
            }
            else
            {
                RoleBadgeText = "访客";
                RoleBadgeFgBrushKey = "ColorBrushGray3";
                RoleBadgeBgBrushKey = "ColorBrushGray5";
            }

            // Permission matrix
            if (isServerSide)
            {
                CanKick = !IsHost;
                CanBlock = !IsHost;
                CanSetAdmin = !IsHost && !IsAdmin;
                CanRevokeAdmin = !IsHost && IsAdmin;
                CanManage = CanKick || CanBlock || CanSetAdmin || CanRevokeAdmin || CanToggleWhitelist || CanResetDeviceBinding;
            }
            else if (isLocalAdmin)
            {
                CanKick = !IsHost && !IsAdmin && !IsLocalMe;
                CanBlock = false;
                CanSetAdmin = false;
                CanRevokeAdmin = false;
                CanManage = CanKick;
            }
            else
            {
                CanKick = false;
                CanBlock = false;
                CanSetAdmin = false;
                CanRevokeAdmin = false;
                CanManage = false;
            }

            // Pre-compute clean, null-safe ToolTip content
            var tip = new StringBuilder();
            tip.Append("昵称：").Append(profile.Name);
            if (!string.IsNullOrEmpty(profile.GameName))
                tip.Append(Environment.NewLine).Append("游戏内名称：").Append(profile.GameName);
            tip.Append(Environment.NewLine).Append("角色：").Append(RoleBadgeText);
            if (IsLocalMe)
                tip.Append("（我）");

            // 权限访问控制：仅具备管理特权的角色（房主与管理员）可见账号验证 (UUID) 与设备特征码 (MachineId)
            // 普通访客视角下移除上述敏感信息，保护玩家隐私与网络安全
            if (isServerSide || isLocalAdmin)
            {
                tip.Append(Environment.NewLine).Append("账号验证：")
                    .Append(IsOfflineMode ? "离线模式（无正版 UUID）" : $"微软正版 ({profile.Uuid})");
                tip.Append(Environment.NewLine).Append("设备特征码：").Append(profile.MachineId);
                if (isServerSide && !IsHost)
                    tip.Append(Environment.NewLine).Append("白名单状态：").Append(IsWhitelisted ? "已加入白名单" : "未加入白名单");
            }

            if (profile.LatencyMs.HasValue)
                tip.Append(Environment.NewLine).Append("延迟：").Append(profile.LatencyMs.Value.ToString("0")).Append(" ms");
            if (CanManage)
                tip.Append(Environment.NewLine).Append("（右键弹出菜单可进行管理操作）");

            TooltipText = tip.ToString();
        }
    }
}
