// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

#nullable enable
using System;
using System.ComponentModel;
using System.IO;

namespace PCLCS.Link.Runtime
{
    /// <summary>
    /// Diagnoses security software (Windows Defender, 360, Huorong, etc.) false-positive interceptions.
    /// </summary>
    public static class SecurityInterceptionDetector
    {
        private const int ErrorAccessDenied = 5;              // ERROR_ACCESS_DENIED
        private const int ErrorAccessDisabledByPolicy = 1260; // ERROR_ACCESS_DISABLED_BY_POLICY
        private const int ErrorVirusInfected = 225;           // ERROR_VIRUS_INFECTED (Windows Defender quarantine code)

        public static bool IsInterception(Exception? ex, string? targetPath, out string advice)
        {
            advice = string.Empty;
            if (ex == null)
                return false;

            var curr = ex;
            while (curr != null)
            {
                if (curr is Win32Exception win32)
                {
                    int code = win32.NativeErrorCode;
                    if (code == ErrorVirusInfected)
                    {
                        advice = "系统安全防护软件（如 Windows Defender / 杀毒软件）判定联机核心存在风险并将其拦截或隔离。";
                        return true;
                    }
                    if (code == ErrorAccessDisabledByPolicy)
                    {
                        advice = "系统安全策略或杀毒软件已阻止运行联机核心进程。";
                        return true;
                    }
                    if (code == ErrorAccessDenied)
                    {
                        advice = "启动联机核心程序被系统拒绝访问，疑似受到杀毒防护软件的主动拦截保护。";
                        return true;
                    }
                }

                if (curr is UnauthorizedAccessException)
                {
                    advice = "联机组件文件的访问与执行权限被安全防护软件阻止。";
                    return true;
                }

                if (curr is FileNotFoundException && !string.IsNullOrEmpty(targetPath) && !File.Exists(targetPath))
                {
                    advice = "联机核心程序文件疑似被杀毒软件静默移入隔离区或直接删除。";
                    return true;
                }

                curr = curr.InnerException;
            }

            return false;
        }
    }
}
