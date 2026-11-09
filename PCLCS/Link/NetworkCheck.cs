// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PCLCS.Link
{
    public static class NetworkCheck
    {
        public static bool HasPublicIPv6()
        {
            return GetUnicastAddresses().Any(IsGlobalIPv6);
        }

        public static IReadOnlyList<IPAddress> GetIPv4Addresses()
        {
            var list = GetUnicastAddressesWithMeta()
                .Where(t => t.Address.AddressFamily == AddressFamily.InterNetwork)
                .OrderByDescending(t => t.IsPhysical)
                .Select(t => t.Address)
                .Distinct()
                .ToList();

            if (!list.Contains(IPAddress.Loopback))
                list.Add(IPAddress.Loopback);
            return list;
        }

        private static bool IsVirtualOrFilteredInterface(NetworkInterface network)
        {
            if (network.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                network.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                return true;

            string name = (network.Name + " " + network.Description).ToLowerInvariant();
            return name.Contains("vmware") ||
                   name.Contains("virtualbox") ||
                   name.Contains("vethernet") ||
                   name.Contains("wsl") ||
                   name.Contains("tailscale") ||
                   name.Contains("zerotier") ||
                   name.Contains("hyper-v") ||
                   name.Contains("tap-windows");
        }

        private static IEnumerable<(IPAddress Address, bool IsPhysical)> GetUnicastAddressesWithMeta()
        {
            NetworkInterface[] interfaces;
            try
            {
                interfaces = NetworkInterface.GetAllNetworkInterfaces();
            }
            catch (NetworkInformationException)
            {
                yield break;
            }

            foreach (var network in interfaces)
            {
                if (network.OperationalStatus != OperationalStatus.Up)
                    continue;

                bool isPhysical = !IsVirtualOrFilteredInterface(network) &&
                                  (network.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                                   network.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);

                UnicastIPAddressInformationCollection unicast;
                try
                {
                    unicast = network.GetIPProperties().UnicastAddresses;
                }
                catch (NetworkInformationException)
                {
                    continue;
                }

                foreach (var info in unicast)
                    yield return (info.Address, isPhysical);
            }
        }

        private static IEnumerable<IPAddress> GetUnicastAddresses()
        {
            return GetUnicastAddressesWithMeta().Select(t => t.Address);
        }

        private static bool IsGlobalIPv6(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetworkV6 || address.IsIPv6Teredo)
                return false;

            byte first = address.GetAddressBytes()[0];
            return (first & 0xE0) == 0x20;
        }
    }
}
