// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace PCLCS.Link.Runtime
{
    public sealed class EasyTierConfig
    {
        public string NetworkName { get; set; } = "";
        public string NetworkSecret { get; set; } = "";
        public string Hostname { get; set; } = "";
        public string? IPv4 { get; set; }

        public List<string> Peers { get; } = new();
        public List<string> TcpWhitelist { get; } = new();
        public List<string> UdpWhitelist { get; } = new();

        public bool DirectOnly { get; set; }
        public bool HideAddress { get; set; }

        public string ToToml()
        {
            var toml = new StringBuilder();
            toml.AppendLine("hostname = " + Quote(Hostname));
            if (IPv4 != null)
                toml.AppendLine("ipv4 = " + Quote(IPv4));
            else
                toml.AppendLine("dhcp = true");

            toml.AppendLine(HideAddress ? "listeners = []" : "listeners = [\"tcp://0.0.0.0:0\", \"udp://0.0.0.0:0\"]");
            toml.AppendLine("tcp_whitelist = " + Array(TcpWhitelist));
            toml.AppendLine("udp_whitelist = " + Array(UdpWhitelist));
            toml.AppendLine();

            toml.AppendLine("[network_identity]");
            toml.AppendLine("network_name = " + Quote(NetworkName));
            toml.AppendLine("network_secret = " + Quote(NetworkSecret));
            toml.AppendLine();

            foreach (string peer in Peers)
            {
                toml.AppendLine("[[peer]]");
                toml.AppendLine("uri = " + Quote(peer));
                toml.AppendLine();
            }

            toml.AppendLine("[flags]");
            toml.AppendLine("no_tun = true");
            toml.AppendLine("latency_first = true");
            toml.AppendLine("enable_kcp_proxy = true");
            if (EasyTierPackage.IsWindows7)
            {
                toml.AppendLine("enable_quic_proxy = false");
                toml.AppendLine("disable_quic_input = true");
            }
            else
            {
                toml.AppendLine("enable_quic_proxy = true");
            }
            toml.AppendLine("data_compress_algo = 2");
            toml.AppendLine("private_mode = true");
            toml.AppendLine("p2p_only = " + (DirectOnly && !HideAddress ? "true" : "false"));
            if (HideAddress)
            {
                toml.AppendLine("disable_p2p = true");
                toml.AppendLine("disable_udp_hole_punching = true");
                toml.AppendLine("disable_tcp_hole_punching = true");
                toml.AppendLine("disable_sym_hole_punching = true");
                toml.AppendLine("disable_upnp = true");
            }
            return toml.ToString();
        }

        private static string Array(IEnumerable<string> values)
        {
            return "[" + string.Join(", ", values.Select(Quote)) + "]";
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }
    }
}
