using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SalsaNOW
{
    internal static class SteamManager
    {
        // Remote manifest describing the current SalsaSteam build:
        // { "version": "11.15.70.27", "url": "https://salsanowfiles.work/USG/SalsaSteam/SalsaSteam_11.15.70.27.zip", "preserve": [ ... ] }
        private const string ManifestUrl = "https://salsanowfiles.work/jsons/salsasteam.json";
        private const string VersionMarker = ".salsasteam_version";

        // Paths (relative to the SalsaSteam folder) that survive an update.
        // The manifest's "preserve" list is added on top of these.
        private static readonly string[] DefaultPreserved =
        {
            "userdata",
            "config",
            "steamapps",
            "logs",
            "dumps",
        };

        private const string SystemSteamDir = @"C:\Program Files (x86)\Steam";
        private const string SystemSteamApps = SystemSteamDir + @"\steamapps";
        private const string SalsaSteamFixUrl = "https://salsanowfiles.work/USG/SalsaSteamFix.exe";

        public static async Task InstallSalsaSteamAsync(string globalDirectory)
        {
            string steamDir = Path.Combine(globalDirectory, "SalsaSteam");

            // steamapps is only moved over on a brand-new install; once SalsaSteam exists it already has its own.
            bool alreadyInstalled = Directory.Exists(steamDir);

            await InstallOrUpdateAsync(globalDirectory, steamDir);

            if (alreadyInstalled)
                SalsaLogger.Steam("SalsaSteam folder already exists, skipping steamapps transfer.");
            else
                await TransferSteamAppsAsync(steamDir);
            RegisterSteamProtocol(steamDir);
            await RunSalsaSteamFixAsync(globalDirectory, steamDir);
        }

        // Points the steam:// web protocol and the Valve\Steam keys at SalsaSteam\SalsaSteam.exe
        // instead of C:\Program Files (x86)\Steam.
        // Written under HKCU, which overrides HKCR without needing admin rights.
        private static void RegisterSteamProtocol(string steamDir)
        {
            string salsaSteamExe = Path.Combine(steamDir, "SalsaSteam.exe");

            if (!File.Exists(salsaSteamExe))
            {
                SalsaLogger.Error($"SalsaSteam.exe not found at {salsaSteamExe}, Steam registry entries left unchanged.");
                return;
            }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\steam"))
                {
                    key.SetValue("", "URL:Steam Protocol");
                    key.SetValue("URL Protocol", "");

                    using (RegistryKey icon = key.CreateSubKey("DefaultIcon"))
                        icon.SetValue("", $"\"{salsaSteamExe}\",1");

                    using (RegistryKey command = key.CreateSubKey(@"Shell\Open\Command"))
                        command.SetValue("", $"\"{salsaSteamExe}\" -- \"%1\"");
                }

                SalsaLogger.Steam($"steam:// protocol now points to {salsaSteamExe}");
            }
            catch (Exception ex) { SalsaLogger.Error($"steam:// protocol registration error: {ex.Message}"); }

            // Games and launchers that locate Steam through the registry read these; Steam stores them with forward slashes.
            try
            {
                using (RegistryKey valve = Registry.CurrentUser.CreateSubKey(@"Software\Valve\Steam"))
                {
                    valve.SetValue("SteamExe", salsaSteamExe.Replace('\\', '/'));
                    valve.SetValue("SteamPath", steamDir.TrimEnd('\\').Replace('\\', '/'));
                }

                SalsaLogger.Steam($"Valve\\Steam SteamExe/SteamPath now point to {steamDir}");
            }
            catch (Exception ex) { SalsaLogger.Error($"Valve\\Steam registry update error: {ex.Message}"); }
        }

        // Downloads the latest SalsaSteamFix.exe every launch and starts it once everything else is in place.
        private static async Task RunSalsaSteamFixAsync(string globalDirectory, string steamDir)
        {
            string fixPath = Path.Combine(globalDirectory, "SalsaSteamFix.exe");

            if (!Directory.Exists(steamDir))
            {
                SalsaLogger.Error($"SalsaSteam folder not found at {steamDir}, skipping SalsaSteamFix.");
                return;
            }

            try
            {
                using (var wc = new WebClient())
                {
                    wc.Headers.Add("Cache-Control", "no-cache");
                    await wc.DownloadFileTaskAsync(new Uri(SalsaSteamFixUrl), fixPath);
                }

                await KillSteamAsync();

                Process.Start(new ProcessStartInfo
                {
                    FileName = fixPath,
                    WorkingDirectory = steamDir,
                    UseShellExecute = true
                });

                SalsaLogger.Steam("SalsaSteamFix started. Hiding SalsaNOW console.");
                NativeMethods.ShowWindow(NativeMethods.GetConsoleWindow(), NativeMethods.SW_HIDE);

                FinalBackgroundTasks.ShowOneTimeNotice(
                    Path.Combine(globalDirectory, "SalsaSteamUpgrade.shown"),
                    "Steam has been upgraded to the SalsaSteam client.",
                    "Steam now runs from the following path:\n" +
                    steamDir + "\n\n" +
                    "Your games are safe: games installed in the original Steam were transferred over to SalsaSteam automatically. " +
                    "You don't need to re-download anything.\n\n" +
                    "The original Steam in C:\\Program Files (x86)\\Steam is no longer used.",
                    waitForClose: false,
                    afterFirstRunNotice: true);
            }
            catch (Exception ex) { SalsaLogger.Error($"SalsaSteamFix error: {ex.Message}"); }
        }

        // On GFN, C:\Program Files (x86)\Steam is a redirected view of I:\user\SteamShadow: the process is launched
        // as C:\...\steam.exe, but Windows reports its real image path as I:\user\SteamShadow\Steam.exe.
        private static readonly string[] OriginalSteamRoots =
        {
            SystemSteamDir,
            @"I:\user\SteamShadow",
        };

        private static bool IsOriginalSteamPath(string path)
        {
            foreach (string root in OriginalSteamRoots)
            {
                if (path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) &&
                    !path.StartsWith(root + "\\steamapps\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // Kills the original Steam (C:\Program Files (x86)\Steam / I:\user\SteamShadow) and every helper it spawned from
        // there (steamwebhelper.exe, steamservice.exe, ...). SalsaSteam, games in steamapps and SalsaNOW itself are left running.
        private static async Task KillSteamAsync()
        {
            int self = Process.GetCurrentProcess().Id;

            var targets = new List<Process>();
            foreach (Process p in Process.GetProcesses())
            {
                string path = p.Id == self ? null : GetProcessPath(p.Id);

                bool isOriginalSteam = path != null && IsOriginalSteamPath(path);

                if (isOriginalSteam) targets.Add(p);
                else
                {
                    // Make it visible in the log when a Steam process is left alone and why.
                    if (path != null && p.ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase))
                        SalsaLogger.Steam($"Leaving steam.exe running from {path} (PID {p.Id}).");
                    p.Dispose();
                }
            }

            if (targets.Count == 0)
            {
                SalsaLogger.Steam($"No Steam processes running from {string.Join(" or ", OriginalSteamRoots)}.");
                return;
            }

            // steam.exe first so it can't respawn its helpers, then everything else.
            foreach (Process p in targets.OrderBy(t => t.ProcessName.Equals("steam", StringComparison.OrdinalIgnoreCase) ? 0 : 1))
            {
                using (p)
                {
                    try
                    {
                        if (p.HasExited) continue;
                        string name = p.ProcessName;
                        int pid = p.Id;
                        p.Kill();
                        await Task.Run(() => p.WaitForExit(5000));
                        SalsaLogger.Steam($"Terminated {name}.exe (PID {pid}) from original Steam.");
                    }
                    catch (Exception ex) { SalsaLogger.Error($"Could not terminate {p.ProcessName}.exe: {ex.Message}"); }
                }
            }
        }

        // QueryFullProcessImageName works across 32/64-bit boundaries, unlike Process.MainModule.
        private static string GetProcessPath(int pid)
        {
            IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
            }
            finally { CloseHandle(handle); }
        }

        private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        // Moves the system Steam's steamapps (manifests, common, workshop...) into SalsaSteam\steamapps:
        // copies everything first, then empties the original folder only if every file copied successfully.
        private static async Task TransferSteamAppsAsync(string steamDir)
        {
            try
            {
                if (!Directory.Exists(steamDir))
                {
                    SalsaLogger.Error($"SalsaSteam folder not found at {steamDir}, skipping steamapps transfer.");
                    return;
                }

                if (!Directory.Exists(SystemSteamApps))
                {
                    SalsaLogger.Steam($"No steamapps found at {SystemSteamApps}, skipping transfer.");
                    return;
                }

                string destination = Path.Combine(steamDir, "steamapps");
                Directory.CreateDirectory(destination);

                SalsaLogger.Steam($"Transferring {SystemSteamApps} -> {destination} | PLEASE WAIT, IT WILL TAKE A WHILE...");

                int copied = 0, skipped = 0, failed = 0;
                string sourceRoot = Path.GetFullPath(SystemSteamApps).TrimEnd('\\');

                // Any folder holding SalsaNOW.exe stays exactly where it is: not copied, not deleted.
                List<string> protectedPaths = FindSalsaNowFolders(sourceRoot);
                foreach (string p in protectedPaths)
                    SalsaLogger.Steam($"SalsaNOW.exe found, leaving untouched: {p}");

                // When the only installed game is the one SalsaNOW runs from, there is nothing to move: do nothing at all.
                if (protectedPaths.Count > 0 && !HasUnprotectedGames(sourceRoot, protectedPaths))
                {
                    SalsaLogger.Steam("The only installed game contains SalsaNOW.exe, skipping steamapps transfer.");
                    return;
                }

                await Task.Run(() =>
                {
                    foreach (string dir in Directory.EnumerateDirectories(sourceRoot, "*", SearchOption.AllDirectories))
                    {
                        if (IsProtected(dir, protectedPaths)) continue;
                        Directory.CreateDirectory(Path.Combine(destination, dir.Substring(sourceRoot.Length + 1)));
                    }

                    var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                        .Where(f => !IsProtected(f, protectedPaths));
                    Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 8 }, source =>
                    {
                        string target = Path.Combine(destination, source.Substring(sourceRoot.Length + 1));
                        try
                        {
                            if (IsSameFile(source, target)) { Interlocked.Increment(ref skipped); return; }

                            if (File.Exists(target))
                                File.SetAttributes(target, FileAttributes.Normal);

                            File.Copy(source, target, true);
                            File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
                            Interlocked.Increment(ref copied);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref failed);
                            SalsaLogger.Error($"steamapps transfer: {source} -> {ex.Message}");
                        }
                    });
                });

                string summary = $"steamapps transfer complete ({copied} copied, {skipped} unchanged, {failed} failed).";
                if (failed > 0)
                {
                    // Never delete the source if anything failed to copy, or those files would be lost.
                    SalsaLogger.Error(summary + " Keeping original steamapps.");
                    return;
                }

                SalsaLogger.Steam(summary);
                await Task.Run(() => ClearDirectory(sourceRoot, protectedPaths));
                SalsaLogger.Steam($"Removed original contents of {SystemSteamApps}.");
            }
            catch (Exception ex) { SalsaLogger.Error($"steamapps transfer error: {ex.Message}"); }
        }

        // Returns every game folder under root that holds SalsaNOW.exe anywhere inside it, plus the folder
        // SalsaNOW is running from. A whole common\<game> folder is protected, not just the exe's own subfolder,
        // so the rest of that game is never moved or deleted.
        // If SalsaNOW.exe sits in root itself, only that file is protected (not the whole steamapps).
        private static List<string> FindSalsaNowFolders(string root)
        {
            var result = new List<string>();
            var exes = new List<string>();

            try
            {
                exes.AddRange(Directory.EnumerateFiles(root, "SalsaNOW.exe", SearchOption.AllDirectories));
            }
            catch (Exception ex) { SalsaLogger.Error($"SalsaNOW.exe scan error: {ex.Message}"); }

            // The running exe may be reported through the SteamShadow path or under a different file name.
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
                string entry = GetProtectedEntry(root, exe);
                if (entry != null && !result.Contains(entry, StringComparer.OrdinalIgnoreCase)) result.Add(entry);
            }
            return result;
        }

        // root\common\<game>\...\SalsaNOW.exe -> root\common\<game>; root\<folder>\...\SalsaNOW.exe -> root\<folder>;
        // root\SalsaNOW.exe -> the exe itself.
        private static string GetProtectedEntry(string root, string exe)
        {
            if (!exe.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase)) return null;

            string[] parts = exe.Substring(root.Length + 1).Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length <= 1) return exe;

            int depth = parts[0].Equals("common", StringComparison.OrdinalIgnoreCase) && parts.Length > 2 ? 2 : 1;
            return Path.Combine(root, string.Join("\\", parts.Take(depth)));
        }

        // True when common holds at least one game folder that is not protected.
        private static bool HasUnprotectedGames(string root, List<string> protectedPaths)
        {
            string common = Path.Combine(root, "common");
            if (!Directory.Exists(common)) return false;

            return Directory.EnumerateDirectories(common).Any(d => !IsProtected(d, protectedPaths));
        }

        // True when path is a protected entry or lives inside a protected folder.
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

        // App manifests and the library list stay in the original steamapps (they are still copied to SalsaSteam too).
        private static bool IsKeptInOriginal(string file)
        {
            string name = Path.GetFileName(file);
            return name.EndsWith(".acf", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("libraryfolders.vdf", StringComparison.OrdinalIgnoreCase);
        }

        // Deletes everything inside a folder except protected entries, .acf files and libraryfolders.vdf; keeps the folder itself.
        // Parent folders of protected folders are kept because they are never empty.
        private static void ClearDirectory(string path, List<string> protectedPaths)
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).ToList())
            {
                if (IsProtected(file, protectedPaths) || IsKeptInOriginal(file)) continue;
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }
                catch (Exception ex) { SalsaLogger.Error($"steamapps cleanup: {file} -> {ex.Message}"); }
            }

            // Deepest folders first so children are gone before their parents are checked.
            var dirs = Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories)
                .Where(d => !IsProtected(d, protectedPaths))
                .OrderByDescending(d => d.Length)
                .ToList();

            foreach (string dir in dirs)
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dir).Any())
                        Directory.Delete(dir);
                }
                catch (Exception ex) { SalsaLogger.Error($"steamapps cleanup: {dir} -> {ex.Message}"); }
            }
        }

        // Treat files with the same size and last-write time as unchanged so repeat launches are fast.
        private static bool IsSameFile(string source, string target)
        {
            var dst = new FileInfo(target);
            if (!dst.Exists) return false;
            var src = new FileInfo(source);
            return src.Length == dst.Length && src.LastWriteTimeUtc == dst.LastWriteTimeUtc;
        }

        private static async Task InstallOrUpdateAsync(string globalDirectory, string steamDir)
        {
            string zipPath = Path.Combine(globalDirectory, "SalsaSteam.zip");

            try
            {
                SalsaSteamManifest manifest;
                using (var wc = new WebClient())
                {
                    wc.Headers.Add("Cache-Control", "no-cache");
                    manifest = JsonConvert.DeserializeObject<SalsaSteamManifest>(await wc.DownloadStringTaskAsync(ManifestUrl));
                }

                if (manifest == null || string.IsNullOrWhiteSpace(manifest.url) || string.IsNullOrWhiteSpace(manifest.version))
                {
                    SalsaLogger.Error("SalsaSteam manifest is missing url or version.");
                    return;
                }

                string markerPath = Path.Combine(steamDir, VersionMarker);
                string installed = File.Exists(markerPath) ? File.ReadAllText(markerPath).Trim() : null;

                if (string.Equals(installed, manifest.version, StringComparison.OrdinalIgnoreCase))
                {
                    SalsaLogger.Steam($"SalsaSteam {installed} is up to date.");
                    return;
                }

                SalsaLogger.Steam(installed == null
                    ? $"Installing SalsaSteam {manifest.version}"
                    : $"Updating SalsaSteam {installed} -> {manifest.version}");

                using (var wc = new WebClient())
                    await wc.DownloadFileTaskAsync(new Uri(manifest.url), zipPath);

                // Only create the folder once the zip is actually here, so a failed download never leaves an empty SalsaSteam behind.
                Directory.CreateDirectory(steamDir);

                var preserved = new HashSet<string>(
                    DefaultPreserved.Concat(manifest.preserve ?? new List<string>()).Select(NormalizeRelative),
                    StringComparer.OrdinalIgnoreCase);
                preserved.Add(VersionMarker);

                CleanInstallDirectory(steamDir, preserved);
                ExtractPreserving(zipPath, steamDir, preserved);

                File.WriteAllText(markerPath, manifest.version);
                SalsaLogger.Steam($"SalsaSteam {manifest.version} installed to {steamDir}");
            }
            catch (Exception ex) { SalsaLogger.Error($"SalsaSteam install error: {ex.Message}"); }
            finally
            {
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            }
        }

        // Removes everything from the install folder except preserved top-level entries.
        private static void CleanInstallDirectory(string steamDir, HashSet<string> preserved)
        {
            foreach (string dir in Directory.GetDirectories(steamDir))
            {
                if (IsPreserved(Path.GetFileName(dir), preserved)) continue;
                try { Directory.Delete(dir, true); }
                catch (Exception ex) { SalsaLogger.Error($"SalsaSteam cleanup: {dir} -> {ex.Message}"); }
            }

            foreach (string file in Directory.GetFiles(steamDir))
            {
                if (IsPreserved(Path.GetFileName(file), preserved)) continue;
                try { File.Delete(file); }
                catch (Exception ex) { SalsaLogger.Error($"SalsaSteam cleanup: {file} -> {ex.Message}"); }
            }
        }

        // Extracts the zip, overwriting files, but never touches files under a preserved path that already exist.
        private static void ExtractPreserving(string zipPath, string steamDir, HashSet<string> preserved)
        {
            string root = Path.GetFullPath(steamDir).TrimEnd('\\') + "\\";

            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string relative = NormalizeRelative(entry.FullName);
                    if (relative.Length == 0) continue;

                    string target = Path.GetFullPath(Path.Combine(steamDir, relative));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip guard

                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    if (IsPreserved(relative, preserved) && File.Exists(target)) continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }
        }

        private static bool IsPreserved(string relative, HashSet<string> preserved)
        {
            relative = NormalizeRelative(relative);
            foreach (string p in preserved)
            {
                if (relative.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private static string NormalizeRelative(string path) =>
            (path ?? "").Replace('/', '\\').Trim('\\', ' ');

        private class SalsaSteamManifest
        {
            public string version { get; set; }
            public string url { get; set; }
            public List<string> preserve { get; set; }
        }
    }
}
