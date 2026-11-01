// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

using System;
using System.Collections.Generic;

namespace PCLCS.Link
{
    public sealed class PublicNode
    {
        public PublicNode(string id, string url, string provider, bool limited = false)
        {
            Id = id;
            Url = url;
            Provider = provider;
            IsLimited = limited;
        }

        public string Id { get; }
        public string Url { get; }
        public string Provider { get; }
        public bool IsLimited { get; }
    }

    public static class PublicNodes
    {
        public static IReadOnlyList<PublicNode> All { get; } = new[]
        {
            new PublicNode("sh-ct", "tcp://225284.xyz:11010", "海波"),
            new PublicNode("xm-ct", "tcp://easytier.weiai.org.cn:11010", "为爱，唯爱"),
            new PublicNode("cq-cm", "tcp://183.230.36.171:11010", "Just the Two of Us"),
            new PublicNode("zz-bgp", "udp://et.basd1.de:11010", "宇智波.希"),
            new PublicNode("sh-bgp", "tcp://et.sumnight.top:11010", "夏夜", limited: true),
            new PublicNode("cn-eo", "wss://et.chinokou.cn", "ChinoKou"),
            new PublicNode("yn-cu", "https://et-public.lctn.top", "凉城听暖"),
            new PublicNode("intl-cf", "wss://et.xn--6kr850c.com", "林然"),
            new PublicNode("intl-wk", "https://etnode.zkitefly.eu.org/node2", "zkitefly"),
        };
    }
}
