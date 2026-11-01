// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PCLCS.Link.Minecraft
{
    /// <summary>
    /// Waits for "Open to LAN" on this computer without firewall prompts.
    /// </summary>
    public static class LocalWorldFinder
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);
        private static readonly TimeSpan RetryRejected = TimeSpan.FromSeconds(20);

        public static async Task<int> WaitForLocalWorldAsync(CancellationToken cancellation)
        {
            var rejected = new Dictionary<int, DateTime>();
            while (true)
            {
                cancellation.ThrowIfCancellationRequested();
                foreach (int port in GetJavaListeningPorts())
                {
                    if (rejected.TryGetValue(port, out var since) && DateTime.UtcNow - since < RetryRejected)
                        continue;
                    if (await MinecraftProbe.IsServerAsync(port, TimeSpan.FromSeconds(2)).ConfigureAwait(false))
                        return port;
                    rejected[port] = DateTime.UtcNow;
                }
                await Task.Delay(PollInterval, cancellation).ConfigureAwait(false);
            }
        }

        public static int FindProcess(int port)
        {
            foreach (var (listening, pid) in ReadListeners(AfInet, 24, 8, 20).Concat(ReadListeners(AfInet6, 56, 20, 52)))
            {
                if (listening == port && pid > 0)
                    return pid;
            }
            return 0;
        }

        private static IEnumerable<int> GetJavaListeningPorts()
        {
            var ports = new SortedSet<int>();
            var names = new Dictionary<int, bool>();
            foreach (var (port, pid) in ReadListeners(AfInet, 24, 8, 20).Concat(ReadListeners(AfInet6, 56, 20, 52)))
            {
                if (!names.TryGetValue(pid, out bool isJava))
                {
                    isJava = IsJava(pid);
                    names[pid] = isJava;
                }
                if (isJava)
                    ports.Add(port);
            }
            return ports;
        }

        private static bool IsJava(int pid)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return process.ProcessName.StartsWith("java", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException || ex is Win32Exception)
            {
                return false;
            }
        }

        private static List<(int Port, int Pid)> ReadListeners(int family, int rowSize, int portOffset, int pidOffset)
        {
            var result = new List<(int, int)>();
            int size = 0;
            GetExtendedTcpTable(IntPtr.Zero, ref size, false, family, TcpTableOwnerPidListener, 0);

            for (int attempt = 0; attempt < 3 && size > 0; attempt++)
            {
                IntPtr table = Marshal.AllocHGlobal(size);
                try
                {
                    uint error = GetExtendedTcpTable(table, ref size, false, family, TcpTableOwnerPidListener, 0);
                    if (error == ErrorInsufficientBuffer)
                        continue;
                    if (error != 0)
                        break;

                    int count = Marshal.ReadInt32(table);
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr row = table + 4 + i * rowSize;
                        int rawPort = Marshal.ReadInt32(row, portOffset);
                        int port = ((rawPort & 0xFF) << 8) | ((rawPort >> 8) & 0xFF);
                        result.Add((port, Marshal.ReadInt32(row, pidOffset)));
                    }
                    break;
                }
                finally
                {
                    Marshal.FreeHGlobal(table);
                }
            }
            return result;
        }

        private const int AfInet = 2;
        private const int AfInet6 = 23;
        private const int TcpTableOwnerPidListener = 3;
        private const uint ErrorInsufficientBuffer = 122;

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int family, int tableClass, uint reserved);
    }
}
