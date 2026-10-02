using System;
using System.IO;
using System.Threading;

namespace BmsSerialDemo
{
    internal static class ExportDestinationTests
    {
        internal static int Run()
        {
            string appRoot = Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string root = Path.Combine(appRoot, ".ed-" + Guid.NewGuid().ToString("N"));
            string target = Path.Combine(root, "destination with spaces - 中文");
            if (File.Exists(target)) throw new IOException("Test destination is a file: " + target);
            Directory.CreateDirectory(target);
            string work = null;
            try
            {
                work = ExportNaming.CreateWorkDirectory();
                if (Path.GetFullPath(work).StartsWith(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) throw new Exception("Work directory must not be created under the selected export target.");
                File.WriteAllText(Path.Combine(work, "fixture.txt"), "export payload");
                string final = Path.Combine(target, "BMS_真实_Pack1_20261002-080000_至_20261002-120000");
                ExportNaming.PublishDirectory(work, target, final, CancellationToken.None);
                if (File.ReadAllText(Path.Combine(final, "fixture.txt")) != "export payload") throw new Exception("Directory publication content mismatch.");

                File.WriteAllText(Path.Combine(final, "sentinel.txt"), "keep");
                bool collision = false;
                try { ExportNaming.PublishDirectory(work, target, final, CancellationToken.None); }
                catch (IOException) { collision = true; }
                if (!collision || File.ReadAllText(Path.Combine(final, "sentinel.txt")) != "keep") throw new Exception("Existing directory was overwritten.");

                using (CancellationTokenSource canceled = new CancellationTokenSource())
                {
                    canceled.Cancel(); bool cancellation = false;
                    try { ExportNaming.PublishDirectory(work, target, Path.Combine(target, "canceled"), canceled.Token); }
                    catch (OperationCanceledException) { cancellation = true; }
                    if (!cancellation || Directory.Exists(Path.Combine(target, "canceled"))) throw new Exception("Canceled publication left a result.");
                }

                string sourceFile = Path.Combine(work, "fixture.csv"); File.WriteAllText(sourceFile, "csv payload");
                string targetFile = Path.Combine(target, "named.csv");
                ExportNaming.PublishFile(sourceFile, targetFile, CancellationToken.None);
                if (File.ReadAllText(targetFile) != "csv payload") throw new Exception("File publication content mismatch.");
                bool fileCollision = false;
                try { ExportNaming.PublishFile(sourceFile, targetFile, CancellationToken.None); }
                catch (IOException) { fileCollision = true; }
                if (!fileCollision || File.ReadAllText(targetFile) != "csv payload") throw new Exception("Existing file was overwritten.");

                string badParent = Path.Combine(root, "not a directory"); File.WriteAllText(badParent, "block");
                bool badTarget = false;
                try { ExportNaming.PublishDirectory(work, badParent, Path.Combine(badParent, "result"), CancellationToken.None); }
                catch (Exception e) { badTarget = e is IOException || e is UnauthorizedAccessException; }
                if (!badTarget) throw new Exception("Unwritable destination did not fail clearly.");

                if (Directory.GetDirectories(target, "BMS_导出暂存-*").Length != 0 || Directory.GetFiles(target, "BMS_导出暂存-*").Length != 0) throw new Exception("Submission staging artifacts were not cleaned.");
                return 8;
            }
            finally
            {
                if (work != null) ExportNaming.TryDeleteDirectory(work);
                string fullRoot = Path.GetFullPath(root), parent = Path.GetDirectoryName(fullRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!String.Equals(parent, appRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(fullRoot).StartsWith(".ed-", StringComparison.Ordinal)) throw new InvalidOperationException("Refusing to clean an unexpected test path: " + fullRoot);
                if (Directory.Exists(fullRoot)) Directory.Delete(fullRoot, true);
            }
        }
    }
}
