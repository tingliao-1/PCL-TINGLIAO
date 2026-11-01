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
            var addresses = GetUnicastAddresses().Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList();
            if (!addresses.Contains(IPAddress.Loopback))
                addresses.Add(IPAddress.Loopback);
            return addresses;
        }

        private static IEnumerable<IPAddress> GetUnicastAddresses()
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
                    yield return info.Address;
            }
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
