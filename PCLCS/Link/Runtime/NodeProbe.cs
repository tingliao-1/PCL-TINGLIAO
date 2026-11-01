// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PCLCS.Link.Runtime
{
    public static class NodeProbe
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

        public static async Task<double?> MeasureAsync(string url, CancellationToken cancellation)
        {
            var config = new EasyTierConfig
            {
                NetworkName = "pcl-probe-" + Hex.Random(8),
                NetworkSecret = Hex.Random(16),
                Hostname = "pcl-probe",
                HideAddress = true,
            };
            config.Peers.Add(url);
            config.TcpWhitelist.Add("0");
            config.UdpWhitelist.Add("0");

            using var instance = await Task.Run(() => EasyTierInstance.Start(config), cancellation).ConfigureAwait(false);
            var deadline = DateTime.UtcNow + Timeout;
            double? first = null;
            while (DateTime.UtcNow < deadline && instance.IsAlive)
            {
                await Task.Delay(1000, cancellation).ConfigureAwait(false);
                var peers = await instance.GetPeersAsync(cancellation).ConfigureAwait(false);
                double? latency = peers.Where(p => !p.IsLocal && !p.IsMember && p.LatencyMs > 0)
                    .Select(p => p.LatencyMs).FirstOrDefault();
                if (latency == null)
                    continue;

                if (first == null)
                {
                    first = latency;
                    continue;
                }
                return Math.Min(first.Value, latency.Value);
            }
            return first;
        }
    }
}
