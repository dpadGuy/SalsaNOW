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

            // An earlier copy-based transfer could fill the disk and leave half-copied games in SalsaSteam, which
            // then made the SalsaSteam install fail too. When SalsaSteam already exists but first-time setup isn't
            // done, run it before the install: it clears the stale copies and frees space before extraction needs it.
            if (Directory.Exists(steamDir) && ReadSetupState(steamDir) != SetupDone)
                await TransferSteamAppsAsync(steamDir);

            await InstallOrUpdateAsync(globalDirectory, steamDir);

            if (ReadSetupState(steamDir) == SetupDone)
                SalsaLogger.Steam("SalsaSteam first-time setup already done, skipping steamapps transfer.");
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

        // First-time setup marker in the SalsaSteam folder. The wipe and the move run exactly once, so later
        // launches never wait on a steamapps scan.
        // - missing: setup never ran. SalsaSteam\steamapps is deleted, the marker is written, then the move runs.
        // - "done": setup already ran. The transfer never runs again.
        // The marker is written before the move starts, so even an interrupted move never causes a second wipe
        // (which would delete games that were already moved). Files the move couldn't handle stay in the original.
        private const string SetupMarker = ".salsasteam_setup";
        private const string SetupDone = "done";

        private static string ReadSetupState(string steamDir)
        {
            try
            {
                string path = Path.Combine(steamDir, SetupMarker);
                return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        private static void WriteSetupState(string steamDir, string state)
        {
            File.WriteAllText(Path.Combine(steamDir, SetupMarker), state);
        }

        // First-time setup wipes SalsaSteam\steamapps completely, so everyone starts the transfer from scratch.
        // This removes everything an earlier copy-based transfer left there (half-copied games, shadercache...)
        // and frees that space before the move starts.
        private static void WipeSalsaSteamApps(string destination)
        {
            if (!Directory.Exists(destination)) return;

            try
            {
                ForceDeleteDirectory(destination);
                SalsaLogger.Steam($"Removed {destination}.");
            }
            catch (Exception ex)
            {
                // Something was locked: delete whatever can be deleted so the space is still freed.
                SalsaLogger.Error($"SalsaSteam steamapps wipe: {ex.Message} Deleting what can be deleted.");
                foreach (string file in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).ToList())
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                    }
                    catch (Exception fileEx) { SalsaLogger.Error($"SalsaSteam steamapps wipe: {file} -> {fileEx.Message}"); }
                }
                RemoveEmptyDirectories(destination, new List<string>());
            }
        }

        private static void ForceDeleteDirectory(string path)
        {
            foreach (string file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, true);
        }

        // On GFN, C:\Program Files (x86)\Steam is a redirected view of I:\user\SteamShadow. Moving from the real
        // folder keeps source and destination on the same drive (I:), so each move is an instant rename that
        // needs no extra space. The C:\ view is used when the real folder is not reachable.
        private const string ShadowSteamApps = @"I:\user\SteamShadow\steamapps";

        // Moves the system Steam's steamapps (common, workshop, downloading...) into SalsaSteam\steamapps.
        // Files are MOVED, never copied in bulk: each file leaves the original as soon as it is in SalsaSteam,
        // so huge games never need double the disk space. Same-drive moves are instant renames; cross-drive
        // moves copy one file and delete it right after, so at most a few files exist twice at any moment.
        // App manifests (.acf) and libraryfolders.vdf are copied instead, because the original folder keeps them.
        // Runs once: anything that fails stays in the original steamapps and is not retried.
        private static async Task TransferSteamAppsAsync(string steamDir)
        {
            try
            {
                if (!Directory.Exists(steamDir))
                {
                    SalsaLogger.Error($"SalsaSteam folder not found at {steamDir}, skipping steamapps transfer.");
                    return;
                }

                string sourceRoot = GetSteamAppsSource();
                if (sourceRoot == null)
                {
                    SalsaLogger.Steam($"No steamapps found at {SystemSteamApps}, nothing to transfer.");
                    WriteSetupState(steamDir, SetupDone);
                    return;
                }

                string destination = Path.Combine(steamDir, "steamapps");

                // First-time setup: wipe SalsaSteam\steamapps, then mark setup done BEFORE moving,
                // so neither the wipe nor the move ever runs again.
                if (ReadSetupState(steamDir) == SetupDone) return;

                SalsaLogger.Steam("SalsaSteam first-time setup: removing SalsaSteam\\steamapps and starting the transfer again...");
                await Task.Run(() => WipeSalsaSteamApps(destination));
                WriteSetupState(steamDir, SetupDone);

                Directory.CreateDirectory(destination);

                // Any folder holding SalsaNOW.exe stays exactly where it is: not moved, not deleted.
                List<string> protectedPaths = FindSalsaNowFolders(sourceRoot);
                foreach (string p in protectedPaths)
                    SalsaLogger.Steam($"SalsaNOW.exe found, leaving untouched: {p}");

                // When the only installed game is the one SalsaNOW runs from, there is nothing to move: do nothing at all.
                if (protectedPaths.Count > 0 && !HasUnprotectedGames(sourceRoot, protectedPaths))
                {
                    SalsaLogger.Steam("The only installed game contains SalsaNOW.exe, skipping steamapps transfer.");
                    return;
                }

                SalsaLogger.Steam($"Moving {sourceRoot} -> {destination} | PLEASE WAIT, IT CAN TAKE A WHILE...");

                int moved = 0, folders = 0, failed = 0;

                await Task.Run(() =>
                {
                    // 1. Whole game folders that SalsaSteam doesn't have yet: one rename per game when on the same drive.
                    string common = Path.Combine(sourceRoot, "common");
                    if (Directory.Exists(common))
                    {
                        foreach (string game in Directory.GetDirectories(common))
                        {
                            if (IsProtected(game, protectedPaths)) continue;

                            string target = Path.Combine(destination, "common", Path.GetFileName(game));
                            if (Directory.Exists(target)) continue;

                            try
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(target));
                                Directory.Move(game, target);
                                folders++;
                                SalsaLogger.Steam($"Moved game folder: {Path.GetFileName(game)}");
                            }
                            catch
                            {
                                // Different drive or locked: it is moved file by file below instead.
                            }
                        }
                    }

                    // 2. Everything left, file by file.
                    var files = Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                        .Where(f => !IsProtected(f, protectedPaths))
                        .ToList();

                    Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, source =>
                    {
                        string target = Path.Combine(destination, source.Substring(sourceRoot.Length + 1));
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target));

                            if (IsKeptInOriginal(source))
                            {
                                if (!IsSameFile(source, target))
                                {
                                    if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                                    File.Copy(source, target, true);
                                    File.SetLastWriteTimeUtc(target, File.GetLastWriteTimeUtc(source));
                                }
                                return;
                            }

                            File.SetAttributes(source, FileAttributes.Normal);

                            if (File.Exists(target))
                            {
                                // Already there from an earlier run: just free the original.
                                if (IsSameFile(source, target))
                                {
                                    File.Delete(source);
                                    Interlocked.Increment(ref moved);
                                    return;
                                }

                                File.SetAttributes(target, FileAttributes.Normal);
                                File.Delete(target);
                            }

                            File.Move(source, target);
                            Interlocked.Increment(ref moved);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref failed);
                            SalsaLogger.Error($"steamapps move: {source} -> {ex.Message}");
                        }
                    });

                    RemoveEmptyDirectories(sourceRoot, protectedPaths);
                });

                string summary = $"steamapps move complete ({folders} game folders, {moved} files moved, {failed} failed).";
                if (failed > 0)
                {
                    // Nothing is lost: failed files are still in the original steamapps.
                    SalsaLogger.Error(summary + " Files that couldn't be moved stay in the original steamapps.");
                    return;
                }

                SalsaLogger.Steam(summary + " SalsaSteam first-time setup done.");
            }
            catch (Exception ex) { SalsaLogger.Error($"steamapps transfer error: {ex.Message}"); }
        }

        // The real I:\user\SteamShadow\steamapps when it can be read (same drive as SalsaSteam), else the C:\ view.
        private static string GetSteamAppsSource()
        {
            foreach (string root in new[] { ShadowSteamApps, SystemSteamApps })
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    Directory.EnumerateFileSystemEntries(root).Any();
                    return Path.GetFullPath(root).TrimEnd('\\');
                }
                catch
                {
                }
            }
            return null;
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

        // App manifests and the library list stay in the original steamapps (they are copied to SalsaSteam, not moved).
        private static bool IsKeptInOriginal(string file)
        {
            string name = Path.GetFileName(file);
            return name.EndsWith(".acf", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("libraryfolders.vdf", StringComparison.OrdinalIgnoreCase);
        }

        // Deletes empty folders left behind by the move, deepest first; keeps the steamapps folder itself.
        // Parent folders of protected folders are kept because they are never empty.
        private static void RemoveEmptyDirectories(string path, List<string> protectedPaths)
        {
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
                preserved.Add(SetupMarker);

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
