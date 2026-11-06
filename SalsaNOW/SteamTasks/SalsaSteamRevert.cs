using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SalsaNOW
{
    // Undoes the SalsaSteam client from V1.6.9.3 / V1.6.9.3-hotfix1 and goes back to the original Steam.
    // - steam:// and HKCU\Software\Valve\Steam are pointed back at the original Steam.
    // - Games in I:\Apps\SalsaNOW\SalsaSteam\steamapps are MOVED back into the original Steam's steamapps.
    //   Moves never copy in bulk, so storage never fills up: same-drive moves are instant renames, and
    //   cross-drive moves copy one file and delete it right after.
    // - The SalsaSteam folder itself is never deleted: SalsaSteam is maintained in its own project.
    //   Once SalsaSteam\steamapps is empty it is removed, so later launches skip this step at no cost.
    //   Files that fail stay put and are moved on the next launch.
    internal static class SalsaSteamRevert
    {
        private const string SystemSteamDir = @"C:\Program Files (x86)\Steam";
        private const string SystemSteamApps = SystemSteamDir + @"\steamapps";

        // On GFN, C:\Program Files (x86)\Steam is a redirected view of I:\user\SteamShadow. Moving into the real
        // folder keeps source and destination on the same drive (I:), so whole game folders move instantly.
        private const string ShadowSteamApps = @"I:\user\SteamShadow\steamapps";

        private static readonly string[] OriginalSteamRoots =
        {
            SystemSteamDir,
            @"I:\user\SteamShadow",
        };

        public static async Task RunAsync(string globalDirectory)
        {
            string salsaSteamDir = Path.Combine(globalDirectory, "SalsaSteam");

            RestoreSteamRegistry();

            if (!Directory.Exists(salsaSteamDir))
                return;

            // The SalsaSteam client itself (folder, SalsaSteamFix.exe, updates) is managed by its own project
            // and is left alone here. Only its steamapps is emptied back into the original Steam.
            try
            {
                int failed = await Task.Run(() => MoveGamesBack(Path.Combine(salsaSteamDir, "steamapps")));

                if (failed > 0)
                    SalsaLogger.Error($"{failed} SalsaSteam files could not be moved back. They stay in {salsaSteamDir}\\steamapps and will be moved next launch.");
            }
            catch (Exception ex) { SalsaLogger.Error($"SalsaSteam move back error: {ex.Message}"); }
        }

        // SalsaSteam pointed steam:// and the Valve\Steam keys at itself under HKCU. Remove that so Windows and
        // games find the original Steam again. Only entries that point at SalsaSteam are touched.
        private static void RestoreSteamRegistry()
        {
            try
            {
                string command = null;
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\steam\Shell\Open\Command"))
                    command = key?.GetValue("") as string;

                if (command != null && command.IndexOf("SalsaSteam", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\steam", false);
                    SalsaLogger.Steam("steam:// protocol restored to the original Steam.");
                }
            }
            catch (Exception ex) { SalsaLogger.Error($"steam:// protocol restore error: {ex.Message}"); }

            try
            {
                using (RegistryKey valve = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam", true))
                {
                    string steamExe = valve?.GetValue("SteamExe") as string;
                    if (steamExe != null && steamExe.IndexOf("SalsaSteam", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        valve.SetValue("SteamExe", (SystemSteamDir + @"\steam.exe").Replace('\\', '/').ToLowerInvariant());
                        valve.SetValue("SteamPath", SystemSteamDir.Replace('\\', '/').ToLowerInvariant());
                        SalsaLogger.Steam("Valve\\Steam SteamExe/SteamPath restored to the original Steam.");
                    }
                }
            }
            catch (Exception ex) { SalsaLogger.Error($"Valve\\Steam registry restore error: {ex.Message}"); }
        }

        // Returns the number of files that could not be moved.
        private static int MoveGamesBack(string sourceRoot)
        {
            if (!Directory.Exists(sourceRoot))
                return 0;

            string destRoot = GetOriginalSteamApps();
            Directory.CreateDirectory(destRoot);

            // Games containing SalsaNOW.exe in the original Steam are never overwritten; SalsaSteam copies of them are dropped.
            List<string> protectedPaths = FindSalsaNowFolders(destRoot);
            foreach (string p in protectedPaths)
                SalsaLogger.Steam($"SalsaNOW.exe found, leaving untouched: {p}");

            SalsaLogger.Steam($"Moving SalsaSteam games back: {sourceRoot} -> {destRoot} | PLEASE WAIT, IT CAN TAKE A WHILE...");

            int folders = 0, moved = 0, failed = 0;

            // 1. Whole game folders the original Steam doesn't have: one rename per game when on the same drive.
            string common = Path.Combine(sourceRoot, "common");
            if (Directory.Exists(common))
            {
                foreach (string game in Directory.GetDirectories(common))
                {
                    string target = Path.Combine(destRoot, "common", Path.GetFileName(game));
                    if (Directory.Exists(target)) continue;

                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        Directory.Move(game, target);
                        folders++;
                        SalsaLogger.Steam($"Moved game folder back: {Path.GetFileName(game)}");
                    }
                    catch
                    {
                        // Different drive or locked: it is moved file by file below instead.
                    }
                }
            }

            // 2. Everything left, file by file.
            var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories).ToList();
            Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, source =>
            {
                string target = Path.Combine(destRoot, source.Substring(sourceRoot.Length + 1));
                try
                {
                    File.SetAttributes(source, FileAttributes.Normal);

                    // SalsaSteam's library list points at the SalsaSteam folder; the original keeps its own.
                    // SalsaSteam copies of the SalsaNOW game are stale; the original is the one in use.
                    if (Path.GetFileName(source).Equals("libraryfolders.vdf", StringComparison.OrdinalIgnoreCase) ||
                        IsProtected(target, protectedPaths))
                    {
                        File.Delete(source);
                        return;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target));

                    if (File.Exists(target))
                    {
                        if (IsSameFile(source, target))
                        {
                            File.Delete(source);
                            Interlocked.Increment(ref moved);
                            return;
                        }

                        // SalsaSteam has the newer copy (games and their .acf were updated there).
                        File.SetAttributes(target, FileAttributes.Normal);
                        File.Delete(target);
                    }

                    File.Move(source, target);
                    Interlocked.Increment(ref moved);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref failed);
                    SalsaLogger.Error($"SalsaSteam move back: {source} -> {ex.Message}");
                }
            });

            RemoveEmptyDirectories(sourceRoot);
            SalsaLogger.Steam($"SalsaSteam games moved back ({folders} game folders, {moved} files moved, {failed} failed).");
            return failed;
        }

        // The real I:\user\SteamShadow\steamapps when it exists (same drive as SalsaSteam), else the C:\ view.
        private static string GetOriginalSteamApps()
        {
            try
            {
                if (Directory.Exists(ShadowSteamApps))
                {
                    Directory.EnumerateFileSystemEntries(ShadowSteamApps).Any();
                    return ShadowSteamApps;
                }
            }
            catch
            {
            }
            return SystemSteamApps;
        }

        // Every common\<game> folder in root that holds SalsaNOW.exe, plus the folder SalsaNOW is running from.
        private static List<string> FindSalsaNowFolders(string root)
        {
            var result = new List<string>();
            var exes = new List<string>();

            try { exes.AddRange(Directory.EnumerateFiles(root, "SalsaNOW.exe", SearchOption.AllDirectories)); }
            catch (Exception ex) { SalsaLogger.Error($"SalsaNOW.exe scan error: {ex.Message}"); }

            try
            {
                string self = Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);
                foreach (string steamRoot in OriginalSteamRoots)
                {
                    string apps = steamRoot + "\\steamapps\\";
                    if (self.StartsWith(apps, StringComparison.OrdinalIgnoreCase))
                        exes.Add(Path.Combine(root, self.Substring(apps.Length)));
                }
            }
            catch (Exception ex) { SalsaLogger.Error($"SalsaNOW path check error: {ex.Message}"); }

            foreach (string exe in exes)
            {
                if (!exe.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) continue;

                string[] parts = exe.Substring(root.Length + 1).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
                string entry;
                if (parts.Length <= 1) entry = exe;
                else
                {
                    int depth = parts[0].Equals("common", StringComparison.OrdinalIgnoreCase) && parts.Length > 2 ? 2 : 1;
                    entry = Path.Combine(root, string.Join("\\", parts.Take(depth)));
                }

                if (!result.Contains(entry, StringComparer.OrdinalIgnoreCase)) result.Add(entry);
            }
            return result;
        }

        private static bool IsProtected(string path, List<string> protectedPaths)
        {
            path = path.TrimEnd('\\');
            foreach (string p in protectedPaths)
            {
                if (path.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static bool IsSameFile(string source, string target)
        {
            var dst = new FileInfo(target);
            if (!dst.Exists) return false;
            var src = new FileInfo(source);
            return src.Length == dst.Length && src.LastWriteTimeUtc == dst.LastWriteTimeUtc;
        }

        // Deletes empty folders, deepest first, including root itself when it ends up empty.
        private static void RemoveEmptyDirectories(string root)
        {
            var dirs = Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                .OrderByDescending(d => d.Length)
                .ToList();
            dirs.Add(root);

            foreach (string dir in dirs)
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch { }
            }
        }
    }
}
