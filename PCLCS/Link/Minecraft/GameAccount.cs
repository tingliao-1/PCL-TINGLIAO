// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace PCLCS.Link.Minecraft
{
    public sealed class GameAccount
    {
        private GameAccount(string name, string? uuid, bool isPremium, int processId)
        {
            Name = name;
            Uuid = uuid;
            IsPremium = isPremium;
            ProcessId = processId;
        }

        public string Name { get; }
        public string? Uuid { get; }
        public bool IsPremium { get; }
        public int ProcessId { get; }

        public static GameAccount? Current { get; private set; }

        public static async Task<GameAccount?> RefreshAsync()
        {
            GameAccount? account = null;
            if (LinkSettings.ReadGameAccount)
            {
                try
                {
                    account = await Task.Run(Detect).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            if (account == null)
            {
                if (LinkSettings.LauncherAccountIsPremium && !string.IsNullOrEmpty(LinkSettings.LauncherAccountName))
                {
                    account = new GameAccount(
                        LinkSettings.LauncherAccountName!,
                        LinkSettings.LauncherAccountUuid,
                        isPremium: true,
                        processId: 0);
                }
                else if (!string.IsNullOrEmpty(LinkSettings.LauncherAccountName))
                {
                    account = new GameAccount(
                        LinkSettings.LauncherAccountName!,
                        LinkSettings.LauncherAccountUuid,
                        isPremium: false,
                        processId: 0);
                }
            }
            else if (!account.IsPremium && LinkSettings.LauncherAccountIsPremium)
            {
                string effectiveUuid = !string.IsNullOrEmpty(account.Uuid) ? account.Uuid! : LinkSettings.LauncherAccountUuid!;
                account = new GameAccount(
                    account.Name,
                    effectiveUuid,
                    isPremium: true,
                    processId: account.ProcessId);
            }

            Current = account;
            return account;
        }

        private static GameAccount? Detect()
        {
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    if (process.ProcessName.IndexOf("java", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    string? commandLine;
                    try
                    {
                        commandLine = ReadCommandLine(process.Id);
                    }
                    catch
                    {
                        continue;
                    }
                    if (commandLine == null || commandLine.IndexOf("--username", StringComparison.Ordinal) < 0)
                        continue;

                    var arguments = Split(commandLine);
                    string? name = ValueAfter(arguments, "--username");
                    if (string.IsNullOrEmpty(name))
                        continue;

                    string? rawUuid = ValueAfter(arguments, "--uuid");
                    string? uuid = rawUuid?.Replace("-", "").ToLowerInvariant();
                    string? type = ValueAfter(arguments, "--userType");

                    // 判定是否为正版：
                    // 1. 显式带有 --userType msa 或 mojang 参数
                    // 2. 启动器中当前已登录微软正版账号，且名称或 UUID 与进程匹配
                    // 3. UUID 为 32 位十六进制且第 13 位为 '4'（标准 RFC 4122 v4 正版 UUID，离线 UUID 第 13 位恒为 '3'）
                    bool isMsaType = type != null && (type.Equals("msa", StringComparison.OrdinalIgnoreCase) || type.Equals("mojang", StringComparison.OrdinalIgnoreCase));
                    bool matchesLauncher = LinkSettings.LauncherAccountIsPremium &&
                        (!string.IsNullOrEmpty(LinkSettings.LauncherAccountName) && string.Equals(name, LinkSettings.LauncherAccountName, StringComparison.OrdinalIgnoreCase) ||
                         (!string.IsNullOrEmpty(uuid) && string.Equals(uuid, LinkSettings.LauncherAccountUuid, StringComparison.OrdinalIgnoreCase)));
                    bool isV4Uuid = uuid != null && uuid.Length == 32 && uuid[12] == '4';

                    bool premium = isMsaType || matchesLauncher || isV4Uuid;
                    if (premium && string.IsNullOrEmpty(uuid) && LinkSettings.LauncherAccountIsPremium)
                        uuid = LinkSettings.LauncherAccountUuid;

                    return new GameAccount(name!, uuid, premium, process.Id);
                }
            }
            return null;
        }

        private static string? ValueAfter(IReadOnlyList<string> arguments, string option)
        {
            for (int i = 0; i + 1 < arguments.Count; i++)
            {
                if (arguments[i] == option)
                    return arguments[i + 1];
            }
            return null;
        }

        internal static List<string> Split(string commandLine)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool quoted = false;
            bool hasToken = false;
            foreach (char c in commandLine)
            {
                if (c == '"')
                {
                    quoted = !quoted;
                    hasToken = true;
                }
                else if (char.IsWhiteSpace(c) && !quoted)
                {
                    if (hasToken)
                        result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                else
                {
                    current.Append(c);
                    hasToken = true;
                }
            }
            if (hasToken)
                result.Add(current.ToString());
            return result;
        }

        internal static string? ReadCommandLine(int processId)
        {
            if (!_commandLineQuerySupported)
                return ReadCommandLineFromWmi(processId);

            IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero)
                return ReadCommandLineFromWmi(processId);
            try
            {
                int status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out int size);
                if (status == StatusInvalidInfoClass)
                {
                    _commandLineQuerySupported = false;
                    return ReadCommandLineFromWmi(processId);
                }
                if (size <= 0)
                    return ReadCommandLineFromWmi(processId);

                IntPtr buffer = Marshal.AllocHGlobal(size);
                try
                {
                    if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, size, out _) != 0)
                        return null;

                    int length = (ushort)Marshal.ReadInt16(buffer);
                    IntPtr text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                    if (text == IntPtr.Zero || length < 2)
                        return null;

                    long available = size - (text.ToInt64() - buffer.ToInt64());
                    if (available < 2)
                        return null;
                    int chars = (int)Math.Min(length, available) / 2;
                    return Marshal.PtrToStringUni(text, chars);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private const int ProcessQueryLimitedInformation = 0x1000;
        private const int ProcessCommandLineInformation = 60;
        private const int StatusInvalidInfoClass = unchecked((int)0xC0000003);

        private static bool _commandLineQuerySupported = true;

        private static string? ReadCommandLineFromWmi(int processId)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT CommandLine FROM Win32_Process WHERE ProcessId = " + processId);
                using var results = searcher.Get();
                foreach (ManagementBaseObject process in results)
                {
                    using (process)
                        return process["CommandLine"] as string;
                }
            }
            catch
            {
            }
            return null;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int size, out int returned);
    }
}
