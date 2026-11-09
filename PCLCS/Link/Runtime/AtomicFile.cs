// Copyright (c) 2026 Xocio. Licensed under the GNU General Public License v3.0.
// Based on Campfire Link implementation.

#nullable enable
using System;
using System.IO;
using System.Text;

namespace PCLCS.Link.Runtime
{
    /// <summary>
    /// 提供两阶段安全原子写入与滚动灾备自愈的文件 I/O 工具类。
    /// 解决断电、蓝屏、进程被强杀时的 0 字节截断与数据丢失问题。
    /// </summary>
    public static class AtomicFile
    {
        private static readonly object SyncLock = new();

        /// <summary>
        /// 原子写入文本文件：先写入 .tmp 并强刷物理扇区，再通过原子元数据替换更新目标文件并保留 .bak 快照。
        /// </summary>
        public static void WriteAllTextAtomic(string filePath, string content)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;

            lock (SyncLock)
            {
                try
                {
                    string dir = Path.GetDirectoryName(filePath) ?? "";
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    string tempPath = filePath + ".tmp";
                    string backupPath = filePath + ".bak";

                    // 1. 写入临时文件并强刷物理扇区 (flushToDisk: true)
                    using (var fs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                    using (var writer = new StreamWriter(fs, Encoding.UTF8))
                    {
                        writer.Write(content);
                        writer.Flush();
                        fs.Flush(flushToDisk: true);
                    }

                    // 2. 原语级原子替换与 .bak 备份滚动
                    if (File.Exists(filePath))
                    {
                        try
                        {
                            File.Replace(tempPath, filePath, backupPath, ignoreMetadataErrors: true);
                        }
                        catch
                        {
                            // 降级兼容：复制备份与覆盖替换
                            try { File.Copy(filePath, backupPath, overwrite: true); } catch { }
                            File.Copy(tempPath, filePath, overwrite: true);
                            try { File.Delete(tempPath); } catch { }
                        }
                    }
                    else
                    {
                        File.Move(tempPath, filePath);
                    }
                }
                catch (Exception ex)
                {
                    LinkLog.Warn($"[AtomicFile] 写入失败: {filePath}, 异常: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 安全读取文本文件：主文件损坏或为空时，自动从 .bak 备份文件灾备自愈。
        /// </summary>
        public static string? ReadAllTextWithBackup(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;

            lock (SyncLock)
            {
                string backupPath = filePath + ".bak";

                // 1. 优先尝试读取主文件
                if (File.Exists(filePath))
                {
                    try
                    {
                        string text = File.ReadAllText(filePath, Encoding.UTF8);
                        if (!string.IsNullOrWhiteSpace(text))
                            return text;
                    }
                    catch (Exception ex)
                    {
                        LinkLog.Warn($"[AtomicFile] 主文件读取失败，尝试启用备份恢复: {filePath}, 原因: {ex.Message}");
                    }
                }

                // 2. 主文件损坏、不存在或为空，自动从备份恢复
                if (File.Exists(backupPath))
                {
                    try
                    {
                        string bakText = File.ReadAllText(backupPath, Encoding.UTF8);
                        if (!string.IsNullOrWhiteSpace(bakText))
                        {
                            LinkLog.Info($"[AtomicFile] 成功从备份文件自愈恢复: {backupPath}");
                            try { File.Copy(backupPath, filePath, overwrite: true); } catch { }
                            return bakText;
                        }
                    }
                    catch
                    {
                    }
                }

                return null;
            }
        }
    }
}
