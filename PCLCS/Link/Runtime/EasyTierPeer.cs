// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

using System;
using System.Collections.Generic;
using System.Globalization;

namespace PCLCS.Link.Runtime
{
    public sealed class EasyTierPeer
    {
        public string Hostname { get; private set; } = "";
        public string IPv4 { get; private set; } = "";
        public bool IsLocal { get; private set; }
        public bool IsDirect { get; private set; }
        public double? LatencyMs { get; private set; }
        public string Tunnel { get; private set; } = "";
        public NatKind Nat { get; private set; }

        public bool IsMember => !string.IsNullOrEmpty(IPv4);

        public static EasyTierPeer FromJson(IDictionary<string, object> item)
        {
            string cost = GetString(item, "cost");
            return new EasyTierPeer
            {
                Hostname = GetString(item, "hostname"),
                IPv4 = GetString(item, "ipv4"),
                IsLocal = cost == "Local",
                IsDirect = cost == "p2p",
                LatencyMs = double.TryParse(GetString(item, "lat_ms"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
                    ? lat
                    : (double?)null,
                Tunnel = GetString(item, "tunnel_proto"),
                Nat = ParseNat(GetString(item, "nat_type")),
            };
        }

        public static NatKind ParseNat(string value)
        {
            switch (value)
            {
                case "OpenInternet":
                case "NoPat":
                    return NatKind.Open;
                case "FullCone":
                    return NatKind.FullCone;
                case "Restricted":
                    return NatKind.Restricted;
                case "PortRestricted":
                    return NatKind.PortRestricted;
                case "Symmetric":
                case "SymUdpFirewall":
                case "SymmetricEasyInc":
                case "SymmetricEasyDec":
                    return NatKind.Symmetric;
                default:
                    return NatKind.Unknown;
            }
        }

        private static string GetString(IDictionary<string, object> item, string key)
        {
            return item.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";
        }
    }

    public enum NatKind
    {
        Unknown,
        Open,
        FullCone,
        Restricted,
        PortRestricted,
        Symmetric,
    }
}
