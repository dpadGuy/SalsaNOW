using IWshRuntimeLibrary;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
using System.Windows.Forms;
using File = System.IO.File;

// ╔══════════════════════════════════════════════════════════════════════════╗
// ║  Rewrite notes vs. original:                                             ║
// ║                                                                          ║
// ║  FIXED:                                                                  ║
// ║  • All Thread.Sleep() in async context → await Task.Delay()              ║
// ║  • WebClient instances in parallel tasks are now properly disposed       ║
// ║  • seelenui first-install: config downloaded once, not per ini line      ║
// ║  • seelenui re-install: Directory.Delete guarded with existence check    ║
// ║  • WinXShell infinite spin-loops → async timeout helper                  ║
// ║  • ShortcutsSaving: startMenuPath.Length on desktop paths → crash fixed  ║
// ║  • ShortcutsSaving: `break` in backup loop → `continue`                  ║
// ║  • BrickPrevention: StreamReader not disposed → File.ReadAllText         ║
// ║  • GameSavesSetup: WebClient not disposed, junction loop has timeout     ║
// ║  • SteamServerShutdown: Process.Start null-guard added                   ║
// ║  • DesktopInstall: duplicate File.Delete for WinXShell zip removed       ║
// ║  • PhotoOfTheDayBingWallpaper: null/empty list guard on bingPhoto[0]     ║
// ║    if it stops working unexpectedly                                      ║
// ║  • Shortcut creation extracted to reusable helper (was duplicated 6x)    ║
// ║  • ShowConsole() helper extracted (was duplicated 3x)                    ║
// ╚══════════════════════════════════════════════════════════════════════════╝

namespace SalsaNOW
{
    internal class Program
    {
        private static string globalDirectory = "";
        private static readonly string currentPath = Directory.GetCurrentDirectory();
        private static string customAppsJsonPath = null;

        // ── Windows APIs ─────────────────────────────────────────────────────
        // Import the FindWindow function from user32.dll

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        // Import the PostMessage function from user32.dll
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", EntryPoint = "FindWindow", SetLastError = true)]
        static extern IntPtr FindWindowByCaption(IntPtr ZeroOnly, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll", SetLastError = true)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool PostMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        const int WM_CLOSE = 0x0010;
        const int SW_HIDE  = 0;
        const int SW_SHOW  = 5;

        // ── Nvidia APIs ──────────────────────────────────────────────────────
        // Credit: https://github.com/mercuryy-1337/

        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
     
        private static extern IntPtr NvAPI_QueryInterface(uint id);
        // important nvapi function ids
        private const uint ID_NvAPI_Initialize             = 0x0150E828;
        private const uint ID_NvAPI_Unload                 = 0xD22BDD7E;
        private const uint ID_NvAPI_DRS_CreateSession      = 0x0694D52E;
        private const uint ID_NvAPI_DRS_DestroySession     = 0xDAD9CFF8;
        private const uint ID_NvAPI_DRS_LoadSettings       = 0x375DBD6B;
        private const uint ID_NvAPI_DRS_SaveSettings       = 0xFCBC7E14;
        private const uint ID_NvAPI_DRS_RestoreAllDefaults = 0x5927B094;
        // sanity check
        private const int NVAPI_OK = 0;

        //Delegate signatures matching the NVAPI Cdecl calling convention
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Del_NvAPI_Initialize();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Del_NvAPI_Unload();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Del_NvAPI_DRS_CreateSession(out IntPtr hSession);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Del_NvAPI_DRS_DestroySession(IntPtr hSession);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Del_NvAPI_DRS_LoadSettings(IntPtr hSession);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Del_NvAPI_DRS_SaveSettings(IntPtr hSession);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Del_NvAPI_DRS_RestoreAllDefaults(IntPtr hSession);

        private static T GetDelegate<T>(uint id) where T : Delegate
        {
            IntPtr ptr = NvAPI_QueryInterface(id);
            if (ptr == IntPtr.Zero)
                // Should always work in GFN | added as guard against future Nvidia patches
                throw new EntryPointNotFoundException(
                    $"NVAPI function 0x{id:X8} could not be resolved. " +
                    "Ensure an NVIDIA GPU driver is installed.");
            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        // ════════════════════════════════════════════════════════════════════
        // ENTRY POINT
        // ════════════════════════════════════════════════════════════════════

        static async Task Main(string[] args)
        {
            Console.Title = "SalsaNOW V1.6.3 - by dpadGuy";

            // First EnableRTX – restore NVAPI defaults before anything touches Steam
            EnableRTX();

            // Parse command-line args for optional custom apps JSON path
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--apps-json" || args[i] == "-a") && i + 1 < args.Length)
                {
                    customAppsJsonPath = args[i + 1];
                    Console.WriteLine($"[+] Custom apps JSON path set: {customAppsJsonPath}");
                    i++;
                }
            }

            // SSL/TLS issue bypass
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
            ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, errors) => true;

            await Startup();

            _ = Task.Run(() => ShortcutsSaving());
            _ = Task.Run(() => TerminateGFNExplorerShell());

            await AppsInstall();
            await DesktopInstall();
            await AppsInstallSilent();
            await SteamServerShutdown();

            // Second EnableRTX – honestly just left it in here cause IDK if Steam Server shutdown can alter NVAPI driver profiles in any way (it shouldnt lol)
            EnableRTX();

            // Hide console window
            var handle = GetConsoleWindow();
            ShowWindow(handle, SW_HIDE);

            _ = Task.Run(async () => await GameSavesSetup());
            StartupBatchConfig();

            _ = Task.Run(() => BrickPrevention());

            await Task.Delay(Timeout.Infinite);
        }

        // ════════════════════════════════════════════════════════════════════
        // STARTUP
        // ════════════════════════════════════════════════════════════════════

        static async Task Startup()
        {
            const string jsonUrl = "https://salsanowfiles.work/jsons/directory.json";

            try
            {
                if (!Directory.Exists("C:\\Asgard"))
                {
                    Console.WriteLine("[!] SalsaNOW detected the host as not being a GeForce NOW environment. Exiting...");
                    await Task.Delay(5000);
                    Environment.Exit(0);
                }

                using (var webClient = new WebClient())
                {
                    string json = await webClient.DownloadStringTaskAsync(jsonUrl);
                    var directory = JsonConvert.DeserializeObject<List<SavePath>>(json);
                    var dir = directory[0];

                    globalDirectory = dir.directoryCreate;
                    Directory.CreateDirectory(dir.directoryCreate);
                    Console.WriteLine($"[!] Main directory created {dir.directoryCreate}");

                    string configPath = Path.Combine(globalDirectory, "SalsaNOWConfig.ini");
                    if (!File.Exists(configPath))
                    {
                        await webClient.DownloadFileTaskAsync(
                            new Uri("https://salsanowfiles.work/jsons/SalsaNOWConfig.ini"),
                            configPath);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Console.ReadKey();
                Environment.Exit(0);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // APPS INSTALL
        // ════════════════════════════════════════════════════════════════════

        static async Task AppsInstall()
        {
            const string jsonUrl = "https://salsanowfiles.work/jsons/apps.json";

            try
            {
                var iniLines = File.ReadAllLines(Path.Combine(globalDirectory, "SalsaNOWConfig.ini"));

                List<Apps> apps;
                using (var wc = new WebClient())
                {
                    string json = await wc.DownloadStringTaskAsync(jsonUrl);
                    apps = JsonConvert.DeserializeObject<List<Apps>>(json);
                }

                // Merge optional local custom apps JSON
                if (!string.IsNullOrEmpty(customAppsJsonPath) && File.Exists(customAppsJsonPath))
                {
                    try
                    {
                        var customApps = JsonConvert.DeserializeObject<List<Apps>>(
                            File.ReadAllText(customAppsJsonPath));
                        if (customApps?.Count > 0)
                        {
                            apps.AddRange(customApps);
                            Console.WriteLine($"[+] Loaded {customApps.Count} custom app(s) from {customAppsJsonPath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[!] Failed to load custom apps JSON: {ex.Message}");
                    }
                }
                else if (!string.IsNullOrEmpty(customAppsJsonPath))
                {
                    Console.WriteLine($"[!] Custom apps JSON file not found: {customAppsJsonPath}");
                }

                // Install all apps in parallel; each task owns and disposes its own WebClient
                var tasks = apps.Select(app => Task.Run(async () =>
                {
                    using (var webClient = new WebClient())
                    {
                        string desktopLnk   = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), $"{app.name}.lnk");
                        string appFolder    = Path.Combine(globalDirectory, app.name);
                        string appExePath   = Path.Combine(globalDirectory, app.exeName);
                        string appZipExe    = Path.Combine(globalDirectory, app.name, app.exeName);
                        string backupDir    = Path.Combine(globalDirectory, "Backup Shortcuts");
                        string shortcutsDir = Path.Combine(globalDirectory, "Shortcuts");

                        bool shortcutAlreadySaved =
                            File.Exists(Path.Combine(backupDir,    $"{app.name}.lnk")) ||
                            File.Exists(Path.Combine(shortcutsDir, $"{app.name}.lnk"));

                        if (!Directory.Exists(appFolder))
                        {
                            Console.WriteLine("[+] Installing " + app.name);

                            if (app.fileExtension == "zip")
                            {
                                string zipPath = $"{appFolder}.zip";
                                await webClient.DownloadFileTaskAsync(new Uri(app.url), zipPath);
                                ZipFile.ExtractToDirectory(zipPath, appFolder);
                                File.Delete(zipPath);

                                if (!shortcutAlreadySaved)
                                    CreateShortcut(desktopLnk, appZipExe, Path.GetDirectoryName(appZipExe));

                                if (app.run == "true")
                                    Process.Start(appZipExe);
                            }
                            else if (app.fileExtension == "exe")
                            {
                                await webClient.DownloadFileTaskAsync(new Uri(app.url), appExePath);

                                if (!shortcutAlreadySaved)
                                    CreateShortcut(desktopLnk, appExePath, Path.GetDirectoryName(globalDirectory));

                                if (app.run == "true")
                                    Process.Start(appExePath);
                            }
                        }
                        else
                        {
                            Console.WriteLine("[!] " + app.name + " Already exists.");

                            if (app.fileExtension == "zip")
                            {
                                if (!shortcutAlreadySaved)
                                    CreateShortcut(desktopLnk, appZipExe, Path.GetDirectoryName(globalDirectory));

                                if (app.run == "true")
                                    Process.Start(appZipExe);
                            }
                        }
                    }
                })).ToList();

                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Console.ReadKey();
                Environment.Exit(0);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // SILENT APPS INSTALL
        // ════════════════════════════════════════════════════════════════════
        // Background-only programs
        static async Task AppsInstallSilent()
        {
            const string jsonUrl  = "https://salsanowfiles.work/jsons/silentapps.json";
            string silentAppsPath = Path.Combine(globalDirectory, "SilentApps");

            try
            {
                Directory.CreateDirectory(silentAppsPath);

                List<SilentApps> apps;
                using (var wc = new WebClient())
                {
                    string json = await wc.DownloadStringTaskAsync(jsonUrl);
                    apps = JsonConvert.DeserializeObject<List<SilentApps>>(json);
                }

                // Build allowed sets so we can clean up stale files from previous runs
                var allowedFolders = new HashSet<string>(
                    apps.Where(a => a.archive == "true").Select(a => a.name),
                    StringComparer.OrdinalIgnoreCase);

                var allowedFiles = new HashSet<string>(
                    apps.Where(a => a.fileExtension == "exe" || a.fileExtension == "bat")
                        .Select(a => $"{a.fileName}.{a.fileExtension}"),
                    StringComparer.OrdinalIgnoreCase);

                // Remove FOLDERS no longer listed in remote JSON
                foreach (var dir in Directory.GetDirectories(silentAppsPath))
                {
                    string dirName = Path.GetFileName(dir);
                    if (!allowedFolders.Contains(dirName))
                    {
                        try
                        {
                            Console.WriteLine($"[-] Removing unused folder: {dirName}");
                            Directory.Delete(dir, true);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[!] Failed to delete folder {dirName}: {ex.Message}");
                        }
                    }
                }

                // Remove FILES no longer listed in remote JSON
                foreach (var file in Directory.GetFiles(silentAppsPath))
                {
                    string fileName = Path.GetFileName(file);
                    if (!allowedFiles.Contains(fileName))
                    {
                        try
                        {
                            Console.WriteLine($"[-] Removing unused file: {fileName}");
                            File.Delete(file);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[!] Failed to delete file {fileName}: {ex.Message}");
                        }
                    }
                }

                var tasks = apps.Select(app => Task.Run(async () =>
                {
                    using (var webClient = new WebClient())
                    {
                        string appFolder  = Path.Combine(silentAppsPath, app.name);
                        string appPath    = Path.Combine(silentAppsPath, $"{app.fileName}.{app.fileExtension}");
                        string appZipPath = Path.Combine(silentAppsPath, app.name, $"{app.fileName}.{app.fileExtension}");

                        if (app.archive == "true")
                        {
                            if (File.Exists(appZipPath))
                                return; // Already installed

                            Console.WriteLine("[+] Installing " + app.name);
                            string zipPath = $"{appFolder}.zip";
                            await webClient.DownloadFileTaskAsync(new Uri(app.url), zipPath);
                            ZipFile.ExtractToDirectory(zipPath, appFolder);
                            File.Delete(zipPath);

                            if (app.run == "true")
                                Process.Start(appZipPath);

                            return;
                        }

                        if (app.fileExtension == "exe" || app.fileExtension == "bat")
                        {
                            Console.WriteLine("[+] Installing " + app.name);
                            await webClient.DownloadFileTaskAsync(new Uri(app.url), appPath);

                            if (app.run == "true")
                            {
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName        = appPath,
                                    UseShellExecute = true,
                                });
                            }
                        }
                    }
                })).ToList();

                await Task.WhenAll(tasks);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Console.ReadKey();
                Environment.Exit(0);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // DESKTOP INSTALL (WinXShell / seelenui)
        // ════════════════════════════════════════════════════════════════════

        static async Task DesktopInstall()
        {
            const string jsonUrl = "https://salsanowfiles.work/jsons/desktop.json";

            // Force dark theme via registry
            Process.Start(new ProcessStartInfo("cmd.exe",
                "/c reg add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize\" " +
                "/v AppsUseLightTheme /t REG_DWORD /d 0 /f")
            {
                UseShellExecute = true,
            });

            try
            {
                string desktopPath  = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string shortcutsDir = Path.Combine(globalDirectory, "Shortcuts");
                var iniLines        = File.ReadAllLines(Path.Combine(globalDirectory, "SalsaNOWConfig.ini"));

                // Pre-compute ini flags once instead of re-checking inside nested loops
                bool skipSeelenExecution = iniLines.Any(l => l.Contains("SkipSeelenUiExecution = \"0\""));
                bool bingWallpaperEnabled = iniLines.Any(l => l.Contains("BingPhotoOfTheDayWallpaper = \"1\""));

                using (var webClient = new WebClient())
                {
                    string json = await webClient.DownloadStringTaskAsync(jsonUrl);
                    var desktopInfo = JsonConvert.DeserializeObject<List<DesktopInfo>>(json);

                    // Kill GFN's own explorer shell if it's running
                    IntPtr hWndSeelen = FindWindow(null, "CustomExplorer");
                    if (hWndSeelen != IntPtr.Zero)
                        PostMessage(hWndSeelen, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

                    foreach (var desktop in desktopInfo)
                    {
                        string appDir      = Path.Combine(globalDirectory, desktop.name);
                        string zipFile     = Path.Combine(globalDirectory, desktop.name + ".zip");
                        string exePath     = Path.Combine(appDir, desktop.exeName);
                        string roamingPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                        string seelenCfgDir = Path.Combine(roamingPath, "com.seelen.seelen-ui");

                        if (!Directory.Exists(appDir))
                        {
                            // ── Fresh install ─────────────────────────────────
                            await webClient.DownloadFileTaskAsync(new Uri(desktop.url), zipFile);
                            ZipFile.ExtractToDirectory(zipFile, appDir);
                            File.Delete(zipFile); // Deleted once, original had a duplicate delete

                            if (desktop.name.Contains("WinXShell"))
                            {
                                Process.Start(exePath);
                                await Task.Delay(500);
                                // Source: https://stackoverflow.com/a
                                // Posted by Sergey Vyacheslavovich Brunov, modified by community.
                                // Retrieved 2025-11-19, License – CC BY-SA 3.0
                                await CloseWindowWithTimeoutAsync("WinXShell", timeoutMs: 15_000);
                            }

                            if (desktop.name.Contains("seelenui"))
                            {
                                // Download and extract config once, original looped over
                                // every ini line and re-downloaded on each iteration regardless of condition.
                                using (var cfgClient = new WebClient())
                                {
                                    await cfgClient.DownloadFileTaskAsync(new Uri(desktop.zipConfig), zipFile);
                                    try
                                    {
                                        ZipFile.ExtractToDirectory(zipFile, seelenCfgDir);
                                        File.Delete(zipFile);
                                    }
                                    catch { /* already partially extracted */ }
                                }

                                if (skipSeelenExecution)
                                    Process.Start(exePath);
                            }
                        }
                        else
                        {
                            // ── Already installed ─────────────────────────────
                            Console.WriteLine("[!] " + desktop.name + " Already exists.");

                            if (desktop.name.Contains("WinXShell"))
                            {
                                if (bingWallpaperEnabled)
                                    await PhotoOfTheDayBingWallpaper(appDir);

                                Process.Start(exePath);
                                // Source: https://stackoverflow.com/a
                                // Posted by Sergey Vyacheslavovich Brunov, modified by community.
                                // Retrieved 2025-11-19, License – CC BY-SA 3.0
                                await CloseWindowWithTimeoutAsync("WinXShell", timeoutMs: 15_000);
                            }

                            if (desktop.name.Contains("seelenui") && skipSeelenExecution)
                            {
                                // Wipe stale config and re-apply fresh from remote
                                if (Directory.Exists(seelenCfgDir))
                                    Directory.Delete(seelenCfgDir, true);

                                using (var cfgClient = new WebClient())
                                {
                                    await cfgClient.DownloadFileTaskAsync(new Uri(desktop.zipConfig), zipFile);
                                    try
                                    {
                                        ZipFile.ExtractToDirectory(zipFile, seelenCfgDir);
                                        File.Delete(zipFile);
                                    }
                                    catch { /* already partially extracted */ }
                                }

                                Process.Start(exePath);
                            }
                        }
                    }
                }

                // Wait for seelenui to complete its first-run startup sequence
                await Task.Delay(6000);

                if (skipSeelenExecution)
                {
                    var stopwatch = Stopwatch.StartNew();
                    const int seelenTimeoutMs = 7000;
                    bool seelenWallCheckedOnce = false;

                    while (true)
                    {
                        bool settingsFound = false;

                        // --- SETTINGS WINDOW CHECK (looped until found or timeout) ---
                        EnumWindows((hWnd, lParam) =>
                        {
                            EnumChildWindows(hWnd, (child, lp) =>
                            {
                                var sb = new StringBuilder(512);
                                GetWindowText(child, sb, sb.Capacity);
                                if (sb.ToString().Equals("tauri.localhost/settings/index.html",
                                        StringComparison.OrdinalIgnoreCase))
                                {
                                    PostMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                                    settingsFound = true;
                                    Console.WriteLine("[+] Settings window closed.");
                                    return false;
                                }
                                return true;
                            }, IntPtr.Zero);
                            return !settingsFound;
                        }, IntPtr.Zero);

                        await Task.Delay(500);

                        // --- SEELEN_WALL CHECK (only once) ---
                        if (!seelenWallCheckedOnce)
                        {
                            seelenWallCheckedOnce = true;
                            bool seelenWallFound  = false;

                            EnumWindows((hWnd, lParam) =>
                            {
                                EnumChildWindows(hWnd, (child, lp) =>
                                {
                                    var sb = new StringBuilder(512);
                                    GetWindowText(child, sb, sb.Capacity);
                                    if (sb.ToString().Equals("tauri.localhost/seelen_wall/index.html",
                                            StringComparison.OrdinalIgnoreCase))
                                    {
                                        SendMessage(child, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                                        seelenWallFound = true;
                                        Console.WriteLine("[+] Seelen Wall window closed.");
                                        return false;
                                    }
                                    return true;
                                }, IntPtr.Zero);
                                return !seelenWallFound;
                            }, IntPtr.Zero);
                        }

                        if (settingsFound)
                            return;

                        if (stopwatch.ElapsedMilliseconds > seelenTimeoutMs)
                        {
                            Console.WriteLine("[!] Seelen UI failed to start, using WinXShell.");
                            return;
                        }

                        await Task.Delay(500);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Console.ReadKey();
                Environment.Exit(0);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // STEAM SERVER SHUTDOWN
        // ════════════════════════════════════════════════════════════════════

        static async Task SteamServerShutdown()
        {
            /* Steam Server (NVIDIA Made Proxy Interceptor for Steam) "127.10.0.231:9753"
             * Communicates with Steam via proxy, intercepting function calls by either
             * suppressing them or replacing them with custom implementations :).
             * Sending POST /shutdown causes all opted-in GFN games to appear in Steam.
             */

            try
            {
                const string dummyJsonLink = "https://salsanowfiles.work/jsons/kaka.json";
                string usgMaskPath = Path.Combine(globalDirectory, "conhost.exe");

                using (var webClient = new WebClient())
                {
                    try
                    {
                        string response = webClient.UploadString("http://127.10.0.231:9753/shutdown", "POST");
                        Console.WriteLine($"[+] Shutting Down Steam Server: {response}");
                    }
                    catch
                    {
                        Console.WriteLine("[!] Steam Server is not running.");
                    }

                    await webClient.DownloadFileTaskAsync(
                        new Uri(dummyJsonLink),
                        Path.Combine(globalDirectory, "kaka.json"));
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName        = @"C:\Program Files (x86)\Steam\lockdown\server\server.exe",
                    Arguments       = Path.Combine(globalDirectory, "kaka.json"),
                    CreateNoWindow  = true,
                    UseShellExecute = false,
                    WindowStyle     = ProcessWindowStyle.Hidden,
                });

                Directory.Delete(@"C:\Program Files (x86)\Steam\appcache", true);

                // Steam USG Part (Temporary code, will get removed once a patch has been found for this USG)
                using (var webClient = new WebClient())
                {
                    await webClient.DownloadFileTaskAsync(
                        new Uri("https://salsanowfiles.work/USG/bleh.exe"),
                        usgMaskPath);
                }

                // Process.Start can return null if CreateProcess fails
                var usgProcess = Process.Start(usgMaskPath);
                if (usgProcess != null)
                {
                    while (!usgProcess.HasExited)
                        await Task.Delay(1000);
                }

                // Small grace period for OS to release the file handle
                await Task.Delay(200);

                if (File.Exists(usgMaskPath))
                    File.Delete(usgMaskPath);

                //foreach (var process in Process.GetProcessesByName("steam"))
                //    process.Kill();
                //Process.Start("steam://open/library");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Console.ReadKey();
                Environment.Exit(0);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // SHORTCUTS SAVING  (background loop)
        // ════════════════════════════════════════════════════════════════════

        static void ShortcutsSaving()
        {
            string desktopPath  = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string shortcutsDir = Path.Combine(globalDirectory, "Shortcuts");
            string backupDir    = Path.Combine(globalDirectory, "Backup Shortcuts");

            Directory.CreateDirectory(shortcutsDir);
            Directory.CreateDirectory(backupDir);

            // On start: push saved shortcuts back to desktop
            try
            {
                File.Delete(Path.Combine(desktopPath, "desktop.ini"));
                foreach (string shortcut in Directory.GetFiles(shortcutsDir, "*.lnk*", SearchOption.AllDirectories))
                    File.Copy(shortcut, shortcut.Replace(shortcutsDir, desktopPath), overwrite: true);
            }
            catch { }

            try
            {
                File.Delete(Path.Combine(desktopPath, "desktop.ini"));

                while (true)
                {
                    Thread.Sleep(5000);

                    // Restore PeaZip | try Shortcuts first, Backup Shortcuts as fallback
                    try
                    {
                        if (!File.Exists(Path.Combine(desktopPath, "PeaZip File Explorer Archiver.lnk")))
                        {
                            File.Copy(
                                Path.Combine(shortcutsDir, "PeaZip File Explorer Archiver.lnk"),
                                Path.Combine(desktopPath,  "PeaZip File Explorer Archiver.lnk"));
                            MessageBox.Show(
                                "PeaZip File Explorer Archiver is a core component in which it cannot be removed.",
                                "SalsaNOW", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                    catch { }

                    try
                    {
                        if (!File.Exists(Path.Combine(desktopPath, "PeaZip File Explorer Archiver.lnk")))
                        {
                            File.Copy(
                                Path.Combine(backupDir,   "PeaZip File Explorer Archiver.lnk"),
                                Path.Combine(desktopPath, "PeaZip File Explorer Archiver.lnk"));
                            MessageBox.Show(
                                "PeaZip File Explorer Archiver is a core component in which it cannot be removed.",
                                "SalsaNOW", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                    catch { }

                    // Restore System Informer | same two-step fallback
                    try
                    {
                        if (!File.Exists(Path.Combine(desktopPath, "System Informer.lnk")))
                        {
                            File.Copy(
                                Path.Combine(shortcutsDir, "System Informer.lnk"),
                                Path.Combine(desktopPath,  "System Informer.lnk"));
                            MessageBox.Show(
                                "System Informer is a core component in which it cannot be removed.",
                                "SalsaNOW", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                    catch { }

                    try
                    {
                        if (!File.Exists(Path.Combine(desktopPath, "System Informer.lnk")))
                        {
                            File.Copy(
                                Path.Combine(backupDir,   "System Informer.lnk"),
                                Path.Combine(desktopPath, "System Informer.lnk"));
                            MessageBox.Show(
                                "System Informer is a core component in which it cannot be removed.",
                                "SalsaNOW", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                    catch { }

                    // Sync desktop → Shortcuts directory (one-way, no overwrite)
                    var lnkFilesDesktop = Directory.EnumerateFiles(desktopPath, "*.lnk", SearchOption.AllDirectories)
                                                   .ToList(); // Materialise before iterating

                    foreach (var file in lnkFilesDesktop)
                    {
                        try
                        {
                            string relativePath = file.Substring(desktopPath.Length + 1);
                            string destPath     = Path.Combine(shortcutsDir, relativePath);
                            Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                            File.Copy(file, destPath, overwrite: false);
                            Console.WriteLine($"[+] Synced: {relativePath}");
                        }
                        catch { }
                    }

                    // NOTE: Start Menu sync loop removed – original used startMenuPath.Length
                    // on desktop file paths, causing ArgumentOutOfRangeException on every file.

                    // Backup shortcuts removed from desktop → Backup Shortcuts directory
                    foreach (var backupFile in Directory.EnumerateFiles(shortcutsDir, "*.lnk", SearchOption.AllDirectories).ToList())
                    {
                        try
                        {
                            string relativePath = backupFile.Substring(shortcutsDir.Length + 1);
                            string originalPath = Path.Combine(desktopPath, relativePath);
                            string fileName     = Path.GetFileName(backupFile);
                            string backupTarget = Path.Combine(backupDir, fileName);

                            if (!File.Exists(originalPath))
                            {
                                if (File.Exists(backupTarget))
                                {
                                    // Already in backup – just remove from Shortcuts.
                                    // Was 'break' in original which exited the whole loop early.
                                    File.Delete(backupFile);
                                    continue;
                                }

                                File.Move(backupFile, backupTarget);
                                Console.WriteLine($"[-] Backed up missing shortcut: {relativePath}");
                            }
                        }
                        catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                ShowConsole();
                Console.WriteLine(ex.ToString());
                Console.ReadKey();
                Environment.Exit(0);
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // TERMINATE GFN EXPLORER SHELL  (background loop)
        // ════════════════════════════════════════════════════════════════════

        // Source: https://stackoverflow.com/a
        // Posted by Sergey Vyacheslavovich Brunov, modified by community.
        // Retrieved 2025-11-19, License – CC BY-SA 3.0
        static void TerminateGFNExplorerShell()
        {
            while (true)
            {
                try
                {
                    Thread.Sleep(500);
                    IntPtr windowPtr = FindWindowByCaption(IntPtr.Zero, "CustomExplorer");
                    if (windowPtr != IntPtr.Zero)
                    {
                        SendMessage(windowPtr, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        Console.WriteLine("[+] CustomExplorer has been closed.");
                    }
                }
                catch (Exception ex)
                {
                    ShowConsole();
                    Console.WriteLine(ex.ToString());
                    Console.ReadKey();
                    Environment.Exit(0);
                }
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // STARTUP BATCH CONFIG
        // ════════════════════════════════════════════════════════════════════

        static void StartupBatchConfig()
        {
            string batchPath = Path.Combine(globalDirectory, "StartupBatch.bat");
            if (File.Exists(batchPath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = batchPath,
                    UseShellExecute = true,
                });
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // BING PHOTO OF THE DAY WALLPAPER
        // ════════════════════════════════════════════════════════════════════

        static async Task PhotoOfTheDayBingWallpaper(string appDir)
        {
            // UHD resolution to avoid compression artifacts.
            // Photos reset daily at 6:00 PM GMT+11 (Canberra, Australia).
            const string jsonUrl   = "https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=en-AU";
            const string domainUrl = "https://www.bing.com";

            try
            {
                using (var webClient = new WebClient())
                {
                    string json     = await webClient.DownloadStringTaskAsync(jsonUrl);
                    var imagesToken = JObject.Parse(json)["images"];
                    if (imagesToken == null) return;

                    var bingPhoto = JsonConvert.DeserializeObject<List<BingPhotoOfTheDay>>(imagesToken.ToString());
                    if (bingPhoto == null || bingPhoto.Count == 0) return;

                    string wallpaperUrl = $"{domainUrl}{bingPhoto[0].urlbase}_UHD.jpg";
                    Console.WriteLine($"[+] Bing photo of the day: {bingPhoto[0].copyright}, {wallpaperUrl}");

                    await webClient.DownloadFileTaskAsync(
                        new Uri(wallpaperUrl),
                        Path.Combine(appDir, "wallpaper.jpg"));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // GAME SAVES SETUP
        // ════════════════════════════════════════════════════════════════════

        static async Task GameSavesSetup()
        {
            const string jsonUrl = "https://salsanowfiles.work/jsons/GameSavesPaths.json";
            string gameSavesPath = Path.Combine(globalDirectory, "Game Saves");

            try
            {
                GamesSavePaths savePaths;
                using (var webClient = new WebClient())
                {
                    string json = await webClient.DownloadStringTaskAsync(jsonUrl);
                    savePaths = JsonConvert.DeserializeObject<GamesSavePaths>(json);
                }

                Directory.CreateDirectory(gameSavesPath);

                foreach (var dir in savePaths.paths)
                {
                    try
                    {
                        string lastDirectory = Path.GetFileName(dir);
                        string craftedPath   = Path.Combine(gameSavesPath, lastDirectory);

                        Directory.CreateDirectory(craftedPath);

                        var psi1 = new ProcessStartInfo("cmd.exe", $"/c rmdir /s /q \"{dir}\"")
                            { UseShellExecute = true };
                        var psi2 = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{dir}\" \"{craftedPath}\"")
                            { UseShellExecute = true };

                        Process.Start(psi1);
                        await Task.Delay(500);
                        Process.Start(psi2);

                        // Public\Documents is kept locked by NVDisplay.Container
                        // close its window so we can delete the folder and create a junction.
                        if (dir.Contains("C:\\Users\\Public\\Documents"))
                        {
                            foreach (var proc in Process.GetProcessesByName("NVDisplay.Container"))
                            {
                                EnumWindows((hWnd, lParam) =>
                                {
                                    GetWindowThreadProcessId(hWnd, out uint windowPid);
                                    if (windowPid == proc.Id)
                                    {
                                        var className = new StringBuilder(256);
                                        GetClassName(hWnd, className, className.Capacity);
                                        if (className.ToString().StartsWith("NvContainerWindowClass",
                                                StringComparison.OrdinalIgnoreCase))
                                        {
                                            Console.WriteLine($"Found: {className}, closing...");
                                            PostMessage(hWnd, WM_CLOSE, 0, 0);
                                        }
                                    }
                                    return true;
                                }, IntPtr.Zero);
                            }

                            // Retry until Documents is deletable and junction is created, or timeout
                            bool junctionCreated = false;
                            var  sw = Stopwatch.StartNew();
                            const int junctionTimeoutMs = 15_000;

                            while (!junctionCreated)
                            {
                                if (sw.ElapsedMilliseconds > junctionTimeoutMs)
                                {
                                    Console.WriteLine($"[!] Timed out trying to create junction for {dir}");
                                    break;
                                }

                                try
                                {
                                    Directory.Delete(dir, true);
                                    if (!Directory.Exists(dir))
                                    {
                                        Process.Start(psi2);
                                        junctionCreated = true;
                                    }
                                }
                                catch { }

                                await Task.Delay(100);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.ToString());
                        Console.ReadKey();
                        Environment.Exit(0);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] GameSavesSetup failed: {ex}");
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // BRICK PREVENTION  (background loop)
        // ════════════════════════════════════════════════════════════════════

        static void BrickPrevention()
        {
            const string userData        = @"C:\Program Files (x86)\Steam\userdata";
            const string fileCheck       = "localconfig.vdf";
            const string blackListedWord = "\"LaunchOptions\"";

            while (true)
            {
                Thread.Sleep(1500);

                try
                {
                    foreach (string currentFile in Directory.EnumerateFiles(
                                 userData, fileCheck, SearchOption.AllDirectories))
                    {
                        // File.ReadAllText disposes the handle correctly even on exceptions,
                        // unlike the original StreamReader which was not disposed on the happy path.
                        string text = File.ReadAllText(currentFile);

                        if (text.Contains(blackListedWord))
                        {
                            File.Delete(currentFile);

                            ShowConsole();
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.WriteLine("[!] STEAM LAUNCH OPTIONS DETECTED, DO NOT USE STEAM LAUNCH OPTIONS, session terminated.");

                            foreach (var process in Process.GetProcessesByName("steam"))
                                process.Kill();
                        }
                    }
                }
                catch { }
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // ENABLE RTX  (NVAPI driver profile reset)
        // ════════════════════════════════════════════════════════════════════

        // Credit: https://github.com/mercuryy-1337/
        static void EnableRTX()
        {
            var initialize = GetDelegate<Del_NvAPI_Initialize>(ID_NvAPI_Initialize);
            int status     = initialize();
            if (status != NVAPI_OK)
                throw new InvalidOperationException($"NvAPI_Initialize failed with status {status}.");

            IntPtr hSession = IntPtr.Zero;
            try
            {
                var createSession = GetDelegate<Del_NvAPI_DRS_CreateSession>(ID_NvAPI_DRS_CreateSession);
                status = createSession(out hSession);
                if (status != NVAPI_OK)
                    throw new InvalidOperationException($"NvAPI_DRS_CreateSession failed with status {status}.");

                var loadSettings = GetDelegate<Del_NvAPI_DRS_LoadSettings>(ID_NvAPI_DRS_LoadSettings);
                status = loadSettings(hSession);
                if (status != NVAPI_OK)
                    throw new InvalidOperationException($"NvAPI_DRS_LoadSettings failed with status {status}.");

                var restoreDefaults = GetDelegate<Del_NvAPI_DRS_RestoreAllDefaults>(ID_NvAPI_DRS_RestoreAllDefaults);
                status = restoreDefaults(hSession);
                if (status != NVAPI_OK)
                    throw new InvalidOperationException($"NvAPI_DRS_RestoreAllDefaults failed with status {status}.");

                var saveSettings = GetDelegate<Del_NvAPI_DRS_SaveSettings>(ID_NvAPI_DRS_SaveSettings);
                status = saveSettings(hSession);
                if (status != NVAPI_OK)
                    throw new InvalidOperationException($"NvAPI_DRS_SaveSettings failed with status {status}.");
            }
            finally
            {
                if (hSession != IntPtr.Zero)
                {
                    try
                    {
                        GetDelegate<Del_NvAPI_DRS_DestroySession>(ID_NvAPI_DRS_DestroySession)(hSession);
                    }
                    catch { /* best-effort cleanup */ }
                }

                try { GetDelegate<Del_NvAPI_Unload>(ID_NvAPI_Unload)(); }
                catch { /* same as above */ }
            }
        }

        // ════════════════════════════════════════════════════════════════════
        // HELPERS
        // ════════════════════════════════════════════════════════════════════
        /// Polls for a window with the given caption and closes it via WM_CLOSE.
        /// Fully async cause it uses await Task.Delay instead of Thread.Sleep.
        /// Has a configurable timeout so the loop cannot spin forever.
    
        static async Task CloseWindowWithTimeoutAsync(string caption, int timeoutMs)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                IntPtr windowPtr = FindWindowByCaption(IntPtr.Zero, caption);
                if (windowPtr != IntPtr.Zero)
                {
                    SendMessage(windowPtr, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    Console.WriteLine($"[+] {caption} error message has been closed.");
                    return;
                }
                await Task.Delay(100);
            }
            Console.WriteLine($"[!] Timeout waiting for '{caption}' window.");
        }

        /// Creates a .lnk shortcut -> extracted from the 6 inline copies in the original
        static void CreateShortcut(string lnkPath, string targetPath, string workingDir)
        {
            try
            {
                var shell    = new WshShell();
                var shortcut = (IWshShortcut)shell.CreateShortcut(lnkPath);
                shortcut.TargetPath       = targetPath;
                shortcut.WorkingDirectory = workingDir;
                shortcut.Save();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[!] Failed to create shortcut at {lnkPath}: {ex.Message}");
            }
        }

        /// Makes the hidden console window visible
        static void ShowConsole()
        {
            ShowWindow(GetConsoleWindow(), SW_SHOW);
        }

        // ════════════════════════════════════════════════════════════════════
        // MODEL CLASSES
        // ════════════════════════════════════════════════════════════════════

        public class SavePath
        {
            public string configName      { get; set; }
            public string directoryCreate { get; set; }
        }

        public class GamesSavePaths
        {
            public List<string> paths { get; set; }
        }

        public class Apps
        {
            public string name          { get; set; }
            public string fileExtension { get; set; }
            public string exeName       { get; set; }
            public string run           { get; set; } 
            public string url           { get; set; }
        }

        public class SilentApps
        {
            public string name          { get; set; }
            public string fileExtension { get; set; }
            public string fileName      { get; set; }
            public string archive       { get; set; } 
            public string run           { get; set; } 
            public string url           { get; set; }
        }

        public class DesktopInfo
        {
            public string name         { get; set; }
            public string exeName      { get; set; }
            public string taskbarFixer { get; set; }
            public string zipConfig    { get; set; }
            public string run          { get; set; }
            public string url          { get; set; }
        }

        public class BingPhotoOfTheDay
        {
            public string urlbase   { get; set; }
            public string copyright { get; set; }
        }
    }
}
