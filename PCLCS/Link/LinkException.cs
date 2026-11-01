// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

using System;
using System.Collections.Generic;

namespace PCLCS.Link
{
    /// <summary>
    /// A multiplayer failure with a localized message.
    /// </summary>
    public class LinkException : Exception
    {
        private static readonly Dictionary<string, string> Messages = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Link.Error.PortNotGame"] = "指定的端口未检测到 Minecraft 游戏服务。",
            ["Link.Error.WorldClosed"] = "单人世界已关闭，联机房间已自动结束。",
            ["Link.Error.ForwardFailed"] = "创建本地网络隧道或端口映射失败。",
            ["Link.Error.HostNoAnswer"] = "房主主机未响应，请检查房主房间是否开启且网络畅通。",
            ["Link.Error.NoDirectPath"] = "无法在双方之间建立 P2P 直连通道。",
            ["Link.Error.NoNodes"] = "未连接到任何可用的联机中继节点。",
            ["Link.Error.RoomNotFound"] = "未找到指定邀请码的房间，请确认邀请码无误且房主房间处于开启状态。",
            ["Link.Error.DownloadFailed"] = "联机依赖组件下载失败，请检查网络连接。",
            ["Link.Error.UnsupportedSystem"] = "当前系统不支持该联机组件。",
            ["Link.Error.RoomBlocked"] = "你已被房主永久拉黑，无法进入该房间。",
            ["Link.Error.RoomRemoved"] = "你已被房主移出房间，请稍后再试。",
            ["Link.Error.RoomLocked"] = "当前房间已被房主锁定，禁止新玩家加入。",
            ["Link.Error.PremiumRequired"] = "该房间已开启【仅正版】模式，未检测到微软正版验证，无法加入。",
        };

        public LinkException(string textKey, params object[] args)
            : this(null, textKey, args)
        {
        }

        public LinkException(Exception? inner, string textKey, params object[] args)
            : base(Format(textKey, args), inner)
        {
            TextKey = textKey;
            TextArgs = args;
        }

        public string TextKey { get; }

        public object[] TextArgs { get; }

        public string LocalizedMessage => Format(TextKey, TextArgs);

        public static string Format(string key, object[] args)
        {
            if (Messages.TryGetValue(key, out string? template))
            {
                try
                {
                    return args != null && args.Length > 0 ? string.Format(template, args) : template;
                }
                catch
                {
                    return template;
                }
            }
            return key;
        }
    }
}
