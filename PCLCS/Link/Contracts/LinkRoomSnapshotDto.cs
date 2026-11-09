// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

#nullable enable
using System;
using System.Collections.Generic;
using PCLCS.Link.Minecraft;

namespace PCLCS.Link.Contracts
{
    /// <summary>
    /// Strongly-typed snapshot of the entire link room state.
    /// Encapsulates all presentation logic and formatting in C# for direct, safe binding by the UI.
    /// </summary>
    public sealed class LinkRoomSnapshotDto
    {
        public bool IsServerSide { get; }
        public string PingText { get; }
        public string PingToolTip { get; }
        public string PlayerCountSummary { get; }
        public string PlayerCountDetail { get; }
        public string HintText { get; }
        public int HintTheme { get; } // 0 = Blue, 1 = Red, 2 = Yellow
        public IReadOnlyList<LinkPlayerViewDto> Players { get; }
        public GameVersionInfo? HostGameVersion { get; }

        public LinkRoomSnapshotDto(
            bool isServerSide,
            string pingText,
            string pingToolTip,
            string playerCountSummary,
            string playerCountDetail,
            string hintText,
            int hintTheme,
            IReadOnlyList<LinkPlayerViewDto> players,
            GameVersionInfo? hostGameVersion = null)
        {
            IsServerSide = isServerSide;
            PingText = pingText ?? "-";
            PingToolTip = pingToolTip ?? "";
            PlayerCountSummary = playerCountSummary ?? "1 人";
            PlayerCountDetail = playerCountDetail ?? "0 人在线";
            HintText = hintText ?? "";
            HintTheme = hintTheme;
            Players = players ?? Array.Empty<LinkPlayerViewDto>();
            HostGameVersion = hostGameVersion;
        }
    }
}
