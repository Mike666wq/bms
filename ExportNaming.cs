using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace BmsSerialDemo
{
    internal static class ExportNaming
    {
        internal static string Stem(string source, int pack, DateTime fromUtc, DateTime toUtc)
        {
            string origin = source == "simulation" ? "模拟" : "真实";
            string packName = pack == 0 ? "全部" : "Pack" + pack.ToString(CultureInfo.InvariantCulture);
            string from = fromUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string to = toUtc.ToLocalTime().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            return "BMS_" + origin + "_" + packName + "_" + from + "_至_" + to;
        }

        internal static string CsvSuggestion(string source, int pack, DateTime fromUtc, DateTime toUtc)
        {
            return Stem(source, pack, fromUtc, toUtc) + "_001.csv";
        }

        internal static string AvailableDirectory(string parent, string stem)
        {
            string candidate = Path.Combine(Path.GetFullPath(parent), stem);
            int batch = 1;
            while (Directory.Exists(candidate) || File.Exists(candidate))
                candidate = Path.Combine(Path.GetFullPath(parent), stem + "_" + (++batch).ToString("000", CultureInfo.InvariantCulture));
            return candidate;
        }

        internal static string CreateWorkDirectory()
        {
            List<string> roots = new List<string>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!String.IsNullOrEmpty(local)) roots.Add(Path.Combine(local, "BmsSerialDemo", "ExportWork"));
            string systemTemp = Path.GetTempPath(); if (!String.IsNullOrEmpty(systemTemp)) roots.Add(systemTemp);
            roots.Add(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "export-work"));
            Exception last = null;
            foreach (string root in roots)
            {
                string path = Path.Combine(root, "BmsSerialDemo-Export-" + Guid.NewGuid().ToString("N"));
                try { Directory.CreateDirectory(path); string probe = Path.Combine(path, "write-check.tmp"); File.WriteAllText(probe, "probe"); File.Delete(probe); string ready = path + "-ready"; Directory.Move(path, ready); return ready; }
                catch (Exception ex) { if (!(ex is IOException) && !(ex is UnauthorizedAccessException)) throw; last = ex; TryDeleteDirectory(path); }
            }
            throw new IOException("无法在当前用户的应用临时目录、系统临时目录或程序目录创建导出工作区。", last);
        }

        internal static void VerifyDestination(string parent)
        {
            if (String.IsNullOrWhiteSpace(parent)) throw new ArgumentException("请选择导出保存目录", "parent");
            string full = Path.GetFullPath(parent), probe = null;
            try
            {
                Directory.CreateDirectory(full);
                probe = Path.Combine(full, "BMS_写入检查-" + Guid.NewGuid().ToString("N") + ".tmp");
                using (FileStream stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.WriteByte(0); stream.Flush(true); }
                File.Delete(probe); probe = null;
            }
            catch (UnauthorizedAccessException ex) { throw new IOException("当前进程无法写入所选导出目录：" + full + "。若从开发工作区运行，请将迁移 ZIP 解压到工作区外的普通目录，再启动其中的程序；开发工作区 EXE 可能继承 Low 完整性标签。", ex); }
            catch (IOException ex) { throw new IOException("无法在所选导出目录创建并清理测试文件：" + full + "。请检查目录权限、磁盘空间和文件占用。", ex); }
            finally { if (probe != null) try { if (File.Exists(probe)) File.Delete(probe); } catch { } }
        }

        internal static void VerifyFileDestination(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("请选择导出文件路径", "path");
            VerifyDestination(Path.GetDirectoryName(Path.GetFullPath(path)));
        }

        internal static void CheckDestinationSpace(string parent)
        {
            string full = Path.GetFullPath(parent), root = Path.GetPathRoot(full);
            long free = new DriveInfo(root).AvailableFreeSpace;
            if (free < 100L * 1024 * 1024) throw new IOException("所选输出卷可用空间低于100 MiB；导出已停止：" + full);
        }

        internal static string PublishDirectory(string workDirectory, string parent, string finalPath, CancellationToken token)
        {
            VerifyDestination(parent);
            if (Directory.Exists(finalPath) || File.Exists(finalPath)) throw new IOException("目标已存在，未覆盖：" + finalPath);
            string stage = CreateSubmissionDirectory(parent);
            try
            {
                CopyDirectory(workDirectory, stage, token);
                token.ThrowIfCancellationRequested();
                if (Directory.Exists(finalPath) || File.Exists(finalPath)) throw new IOException("目标已存在，未覆盖：" + finalPath);
                Directory.Move(stage, finalPath);
                return finalPath;
            }
            catch
            {
                TryDeleteDirectory(stage);
                throw;
            }
        }

        internal static void PublishFile(string workFile, string finalPath, CancellationToken token)
        {
            string parent = Path.GetDirectoryName(Path.GetFullPath(finalPath));
            if (String.IsNullOrEmpty(parent)) throw new ArgumentException("目标文件必须有父目录", "finalPath");
            VerifyDestination(parent);
            if (File.Exists(finalPath) || Directory.Exists(finalPath)) throw new IOException("目标文件已存在，未覆盖：" + finalPath);
            string stage = Path.Combine(parent, "BMS_导出暂存-" + Guid.NewGuid().ToString("N") + ".partial");
            try
            {
                using (FileStream input = new FileStream(workFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (FileStream output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[64 * 1024]; int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
                    output.Flush(true);
                }
                token.ThrowIfCancellationRequested();
                if (File.Exists(finalPath) || Directory.Exists(finalPath)) throw new IOException("目标文件已存在，未覆盖：" + finalPath);
                File.Move(stage, finalPath);
            }
            catch { try { if (File.Exists(stage)) File.Delete(stage); } catch { } throw; }
        }

        internal static void TryDeleteDirectory(string path)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }

        static string CreateSubmissionDirectory(string parent)
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                string path = Path.Combine(Path.GetFullPath(parent), "BMS_导出暂存-" + Guid.NewGuid().ToString("N"));
                try { Directory.CreateDirectory(path); string probe = Path.Combine(path, "write-check.tmp"); File.WriteAllText(probe, "probe"); File.Delete(probe); string ready = path + "-ready"; Directory.Move(path, ready); return ready; }
                catch (IOException) { if (attempt == 9) throw; }
            }
            throw new IOException("无法创建导出提交暂存目录");
        }

        static void CopyDirectory(string source, string target, CancellationToken token)
        {
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested();
                using (FileStream input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (FileStream output = new FileStream(Path.Combine(target, Path.GetFileName(file)), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[64 * 1024]; int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0) { token.ThrowIfCancellationRequested(); output.Write(buffer, 0, read); }
                    output.Flush(true);
                }
            }
            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.TopDirectoryOnly))
            {
                token.ThrowIfCancellationRequested(); string child = Path.Combine(target, Path.GetFileName(directory)); Directory.CreateDirectory(child); CopyDirectory(directory, child, token);
            }
        }
    }
}
