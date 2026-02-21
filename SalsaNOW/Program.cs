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

namespace SalsaNOW
{
    internal class Program
    {
        private static string globalDirectory = "";
        private static string currentPath = Directory.GetCurrentDirectory();
        private static string customAppsJsonPath = null;

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
        static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool PostMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        const int WM_CLOSE = 0x0010;

        const int SW_HIDE = 0;
        const int SW_SHOW = 5;

        static async Task Main(string[] args)
        {
            Console.Title = "SalsaNOW V1.6.3 - by dpadGuy";

            // Parse command-line arguments for custom apps JSON
            for (int i = 0; i < args.Length; i++)
            {
                if ((args[i] == "--apps-json" || args[i] == "-a") && i + 1 < args.Length)
                {
                    customAppsJsonPath = args[i + 1];
                    Console.WriteLine($"[+] Custom apps JSON path set: {customAppsJsonPath}");
                    i++; // Skip the next argument since it's the path
                }
            }

            // Making sure no SSL/TLS issues occur
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
            ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, errors) => true;

            await Startup();

            _ = Task.Run(() => ShortcutsSaving());
            _ = Task.Run(() => TerminateGFNExplorerShell());

            await AppsInstall();
            await AppsInstallSilent();
            await DesktopInstall();
            await SteamServerShutdown();

            // Hide console window
            var handle = GetConsoleWindow();
            ShowWindow(handle, SW_HIDE);

            _ = Task.Run(async () => await GameSavesSetup());
            StartupBatchConfig();

            _ = Task.Run(() => BrickPrevention());

            await Task.Delay(Timeout.Infinite);
        }

        static async Task Startup()
        {
            string jsonUrl = "https://salsanowfiles.work/jsons/directory.json";

            try
            {
                if (!Directory.Exists("C:\\Asgard"))
                {
                    Console.WriteLine("[!] SalsaNOW detected the host as not being a GeForce NOW environment. Exiting...");
                    Thread.Sleep(5000);
                    Environment.Exit(0);
                }

                using (WebClient webClient = new WebClient())
                {
                    string json = await webClient.DownloadStringTaskAsync(jsonUrl);
                    List<SavePath> directory = JsonConvert.DeserializeObject<List<SavePath>>(json);
                    var dir = directory[0];

                    globalDirectory = dir.directoryCreate;
                    Directory.CreateDirectory(dir.directoryCreate);
                    Console.WriteLine($"[!] Main directory created {dir.directoryCreate}");

                    if (!System.IO.File.Exists($"{globalDirectory}\\SalsaNOWConfig.ini"))
                    {
                        await webClient.DownloadFileTaskAsync(new Uri("https://salsanowfiles.work/jsons/SalsaNOWConfig.ini"), $"{globalDirectory}\\SalsaNOWConfig.ini");
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
        static async Task AppsInstall()
        {
            string jsonUrl = "https://salsanowfiles.work/jsons/apps.json";
            string salsaNowIniPath = $"{globalDirectory}\\SalsaNOWConfig.ini";

            try
            {
                var salsaNowIniOpen = System.IO.File.ReadAllLines($"{globalDirectory}\\SalsaNOWConfig.ini");

                // Load built-in apps from remote JSON
                WebClient wc = new WebClient();
                string json = await wc.DownloadStringTaskAsync(jsonUrl);
                List<Apps> apps = JsonConvert.DeserializeObject<List<Apps>>(json);

                // Load custom apps from local JSON if provided via --apps-json argument
                if (!string.IsNullOrEmpty(customAppsJsonPath) && System.IO.File.Exists(customAppsJsonPath))
                {
                    try
                    {
                        string customJson = System.IO.File.ReadAllText(customAppsJsonPath);
                        List<Apps> customApps = JsonConvert.DeserializeObject<List<Apps>>(customJson);
                        if (customApps != null && customApps.Count > 0)
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

                var tasks = apps.Select(app => Task.Run(async () =>
                {
                    WebClient webClient = new WebClient(); // new instance per app

                    string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + $"\\{app.name}.lnk";
                    string zipFile = Path.Combine(globalDirectory, app.name);
                    string appExePath = Path.Combine(globalDirectory, app.exeName);
                    string appZipPath = Path.Combine(globalDirectory, app.name, app.exeName);
                    string backupShortcutsDir = Path.Combine(globalDirectory, "Backup Shortcuts");
                    string shortcutsDir = Path.Combine(globalDirectory, "Shortcuts");

                    if (!Directory.Exists(zipFile))
                    {
                        if (app.fileExtension == "zip")
                        {
                            Console.WriteLine("[+] Installing " + app.name);

                            await webClient.DownloadFileTaskAsync(new Uri(app.url), $"{zipFile}.zip");

                            ZipFile.ExtractToDirectory($"{zipFile}.zip", zipFile);

                            if (!System.IO.File.Exists($"{backupShortcutsDir}\\{app.name}.lnk"))
                            {
                                if (!System.IO.File.Exists($"{shortcutsDir}\\{app.name}.lnk"))
                                {
                                    WshShell shell = new WshShell();
                                    IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(desktopPath);
                                    shortcut.TargetPath = appZipPath;
                                    shortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(appZipPath);

                                    shortcut.Save();
                                }
                            }

                            System.IO.File.Delete($"{zipFile}.zip");

                            if (app.run == "true")
                            {
                                Process.Start(appZipPath);
                            }
                        }

                        if (app.fileExtension == "exe")
                        {
                            Console.WriteLine("[+] Installing " + app.name);

                            await webClient.DownloadFileTaskAsync(new Uri(app.url), appExePath);

                            if (!System.IO.File.Exists($"{backupShortcutsDir}\\{app.name}.lnk"))
                            {
                                if (!System.IO.File.Exists($"{shortcutsDir}\\{app.name}.lnk"))
                                {
                                    WshShell shell = new WshShell();
                                    IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(desktopPath);
                                    shortcut.TargetPath = appExePath;
                                    shortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(globalDirectory);

                                    shortcut.Save();
                                }
                            }

                            if (app.run == "true")
                            {
                                Process.Start(appExePath);
                            }
                        }
                    }
                    else
                    {
                        Console.WriteLine("[!] " + app.name + " Already exists.");

                        if (app.fileExtension == "zip")
                        {
                            if (!System.IO.File.Exists($"{backupShortcutsDir}\\{app.name}.lnk"))
                            {
                                if (!System.IO.File.Exists($"{shortcutsDir}\\{app.name}.lnk"))
                                {
                                    WshShell shell = new WshShell();
                                    IWshShortcut shortcut = (IWshShortcut)shell.CreateShortcut(desktopPath);
                                    shortcut.TargetPath = appZipPath;
                                    shortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(globalDirectory);

                                    shortcut.Save();
                                }
                            }

                            if (app.run == "true")
                            {
                                Process.Start(appZipPath);
                            }
                        }
                    }
                })).ToList();

                await Task.WhenAll(tasks);

                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Console.ReadKey();
                Environment.Exit(0);
            }
        }
        static async Task AppsInstallSilent() // Meant for deploying programs that run in background only, no user interaction.
        {
            string jsonUrl = "https://salsanowfiles.work/jsons/silentapps.json";
            string salsaNowIniPath = $"{globalDirectory}\\SalsaNOWConfig.ini";
            string silentAppsPath = $"{globalDirectory}\\SilentApps";

            try
            {
                var salsaNowIniOpen = System.IO.File.ReadAllLines($"{globalDirectory}\\SalsaNOWConfig.ini");

                Directory.CreateDirectory(silentAppsPath);

                // Load built-in apps from remote JSON
                WebClient wc = new WebClient();
                string json = await wc.DownloadStringTaskAsync(jsonUrl);
                List<SilentApps> apps = JsonConvert.DeserializeObject<List<SilentApps>>(json);

                // Build allowed folder list: include all archive apps
                var allowedFolders = new HashSet<string>(
                    apps.Where(a => a.archive == "true").Select(a => a.name).ToList(),
                    StringComparer.OrdinalIgnoreCase
                );

                // Build allowed file list: include .exe and .bat files
                var allowedFiles = new HashSet<string>(
                    apps.Where(a => a.fileExtension == "exe" || a.fileExtension == "bat")
                        .Select(a => a.fileName + "." + a.fileExtension)
                        .ToList(),
                    StringComparer.OrdinalIgnoreCase
                );

                // Remove folders not in JSON
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

                // Remove files not in JSON
                foreach (var file in Directory.GetFiles(silentAppsPath))
                {
                    string fileName = Path.GetFileName(file);

                    if (!allowedFiles.Contains(fileName))
                    {
                        try
                        {
                            Console.WriteLine($"[-] Removing unused file: {fileName}");
                            System.IO.File.Delete(file);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[!] Failed to delete file {fileName}: {ex.Message}");
                        }
                    }
                }

                var tasks = apps.Select(app => Task.Run(async () =>
                {
                    WebClient webClient = new WebClient(); // new instance per app

                    string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop) + $"\\{app.name}.lnk";
                    string zipFile = Path.Combine(silentAppsPath, app.name);
                    string appPath = Path.Combine(silentAppsPath, app.fileName + "." + app.fileExtension);

                    string appZipPath = Path.Combine(silentAppsPath, app.name, app.fileName + "." + app.fileExtension);

                    if (app.archive == "true")
                    {
                        if (System.IO.File.Exists(appZipPath))
                        {
                            return;
                        }

                        Console.WriteLine("[+] Installing " + app.name);

                        await webClient.DownloadFileTaskAsync(new Uri(app.url), $"{zipFile}.zip");

                        ZipFile.ExtractToDirectory($"{zipFile}.zip", zipFile);

                        System.IO.File.Delete($"{zipFile}.zip");

                        if (app.run == "true")
                        {
                            Process.Start(appZipPath);
                        }

                        return;
                    }

                    if (app.fileExtension == "exe")
                    {
                        Console.WriteLine("[+] Installing " + app.name);

                        await webClient.DownloadFileTaskAsync(new Uri(app.url), appPath);

                        if (app.run == "true")
                        {
                            Process.Start(appPath);
                        }
                    }

                    if (app.fileExtension == "bat")
                    {
                        Console.WriteLine("[+] Installing " + app.name);

                        await webClient.DownloadFileTaskAsync(new Uri(app.url), appPath);

                        if (app.run == "true")
                        {
                            var startInfo = new ProcessStartInfo
                            {
                                FileName = appPath,
                                UseShellExecute = true,
                            };

                            Process.Start(startInfo);
                        }
                    }
                })).ToList();

                await Task.WhenAll(tasks);

                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Console.ReadKey();
                Environment.Exit(0);
            }
        }

        static async Task DesktopInstall()
        {
            string jsonUrl = "https://salsanowfiles.work/jsons/desktop.json";
            string salsaNowIniPath = $"{globalDirectory}\\SalsaNOWConfig.ini";

            ProcessStartInfo psi1 = new ProcessStartInfo("cmd.exe", "/c reg add \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize\" /v AppsUseLightTheme /t REG_DWORD /d 0 /f")
            {
                UseShellExecute = true,
            };

            Process.Start(psi1);

            try
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string shortcutsDir = Path.Combine(globalDirectory, "Shortcuts");
                var salsaNowIniOpen = System.IO.File.ReadAllLines($"{globalDirectory}\\SalsaNOWConfig.ini");

                using (WebClient webClient = new WebClient())
                {
                    string json = await webClient.DownloadStringTaskAsync(jsonUrl);
                    List<DesktopInfo> desktopInfo = JsonConvert.DeserializeObject<List<DesktopInfo>>(json);

                    IntPtr hWndSeelen = FindWindow(null, "CustomExplorer");

                    // Check if the window handle is valid
                    if (hWndSeelen != IntPtr.Zero)
                    {
                        PostMessage(hWndSeelen, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    }

                    foreach (var desktops in desktopInfo)
                    {
                        string appDir = Path.Combine(globalDirectory, desktops.name);
                        string zipFile = Path.Combine(globalDirectory, desktops.name + ".zip");
                        string exePath = Path.Combine(appDir, desktops.exeName);
                        string taskbarFixerPath = string.IsNullOrEmpty(desktops.taskbarFixer) ? "" : Path.Combine(appDir, desktops.taskbarFixer);
                        string roamingPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                        if (!Directory.Exists(appDir))
                        {
                            await webClient.DownloadFileTaskAsync(new Uri(desktops.url), zipFile);

                            ZipFile.ExtractToDirectory(zipFile, appDir);
                            System.IO.File.Delete(zipFile);

                            if (desktops.name.Contains("WinXShell"))
                            {
                                Process.Start(exePath);

                                System.IO.File.Delete(zipFile);

                                Thread.Sleep(500);

                                // Source - https://stackoverflow.com/a
                                // Posted by Sergey Vyacheslavovich Brunov, modified by community. See post 'Timeline' for change history
                                // Retrieved 2025-11-19, License - CC BY-SA 3.0

                                while (true)
                                {
                                    IntPtr windowPtr = FindWindowByCaption(IntPtr.Zero, "WinXShell");
                                    if (windowPtr == IntPtr.Zero)
                                    {
                                        // Do nothing, retry
                                    }
                                    else
                                    {
                                        SendMessage(windowPtr, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                                        Console.WriteLine("[+] WinXShell error message has been closed.");
                                        break;
                                    }
                                }
                            }

                            if (desktops.name.Contains("seelenui"))
                            {
                                foreach (var line in salsaNowIniOpen)
                                {
                                    WebClient webClientConfig = new WebClient();
                                    await webClientConfig.DownloadFileTaskAsync(new Uri(desktops.zipConfig), zipFile);

                                    try
                                    {
                                        ZipFile.ExtractToDirectory(zipFile, $"{roamingPath}\\com.seelen.seelen-ui");
                                        System.IO.File.Delete(zipFile);
                                    }
                                    catch
                                    {
                                    }

                                    if (line.Contains("SkipSeelenUiExecution = \"0\""))
                                    {
                                        Process.Start(exePath);
                                    }
                                }
                            }
                        }
                        else
                        {
                            Console.WriteLine("[!] " + desktops.name + " Already exists.");

                            if (desktops.name.Contains("WinXShell"))
                            {
                                // Bing Photo Of The Day Wallpaper Feature
                                foreach (var line in salsaNowIniOpen)
                                {
                                    if (line.Contains("BingPhotoOfTheDayWallpaper = \"1\""))
                                    {
                                        await PhotoOfTheDayBingWallpaper(appDir);
                                    }
                                }

                                Process.Start(exePath);

                                // Source - https://stackoverflow.com/a
                                // Posted by Sergey Vyacheslavovich Brunov, modified by community. See post 'Timeline' for change history
                                // Retrieved 2025-11-19, License - CC BY-SA 3.0

                                while (true)
                                {
                                    IntPtr windowPtr = FindWindowByCaption(IntPtr.Zero, "WinXShell");
                                    if (windowPtr == IntPtr.Zero)
                                    {
                                        // Do nothing, retry
                                    }
                                    else
                                    {
                                        SendMessage(windowPtr, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                                        Console.WriteLine("[+] WinXShell error message has been closed.");
                                        break;
                                    }
                                }
                            }

                            if (desktops.name.Contains("seelenui"))
                            {
                                foreach (var ln in salsaNowIniOpen)
                                {
                                    if (ln.Contains("SkipSeelenUiExecution = \"0\""))
                                    {
                                        Directory.Delete(roamingPath + "\\com.seelen.seelen-ui", true);

                                        await new WebClient().DownloadFileTaskAsync(new Uri(desktops.zipConfig), zipFile);

                                        try
                                        {
                                            ZipFile.ExtractToDirectory(zipFile, roamingPath + "\\com.seelen.seelen-ui");
                                            System.IO.File.Delete(zipFile);
                                        }
                                        catch
                                        {
                                        }

                                        Process.Start(exePath);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }

                Thread.Sleep(6000);

                foreach (var ln in salsaNowIniOpen)
                {
                    if (ln.Contains("SkipSeelenUiExecution = \"0\""))
                    {
                        Stopwatch stopwatch = Stopwatch.StartNew();
                        int timeoutMs = 7000;

                        bool seelenWallCheckedOnce = false;

                        while (true)
                        {
                            bool settingsFound = false;

                            // --- SETTINGS WINDOW CHECK (looped) ---
                            EnumWindows((hWnd, lParam) =>
                            {
                                EnumChildWindows(hWnd, (child, lp) =>
                                {
                                    var sb = new StringBuilder(512);
                                    GetWindowText(child, sb, sb.Capacity);

                                    if (sb.ToString().Equals("tauri.localhost/settings/index.html", StringComparison.OrdinalIgnoreCase))
                                    {
                                        SendMessage(child, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                                        settingsFound = true;
                                        Console.WriteLine("[+] Settings window closed.");
                                        return false;
                                    }
                                    return true;
                                }, IntPtr.Zero);

                                return !settingsFound;
                            }, IntPtr.Zero);

                            Thread.Sleep(500);

                            // --- SEELEN_WALL CHECK (only ONCE) ---
                            if (!seelenWallCheckedOnce)
                            {
                                seelenWallCheckedOnce = true; // prevent future checks

                                bool seelenWallFound = false;

                                EnumWindows((hWnd, lParam) =>
                                {
                                    EnumChildWindows(hWnd, (child, lp) =>
                                    {
                                        var sb = new StringBuilder(512);
                                        GetWindowText(child, sb, sb.Capacity);

                                        if (sb.ToString().Equals("tauri.localhost/seelen_wall/index.html", StringComparison.OrdinalIgnoreCase))
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


                            // If settings was found, exit loop
                            if (settingsFound)
                                return;

                            // Timeout
                            if (stopwatch.ElapsedMilliseconds > timeoutMs)
                            {
                                Console.WriteLine("[!] Seelen UI failed to start, using WinXShell.");
                                return;
                            }

                            Thread.Sleep(500);
                        }
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

        static async Task SteamServerShutdown()
        {
            /* Steam Server (NVIDIA Made Proxy Interceptor for Steam) "127.10.0.231:9753"
             * Steam Server communicates with Steam by proxy and intercepts function calls from Steam by
             * making them not happen or replaces them with special made ones to do something else.
             * Shutting the server down by POST request will lead to all opted-in games on
             * GeForce NOW to show up on Steam.
             */

            try
            {
                string dummyJsonLink = "https://salsanowfiles.work/jsons/kaka.json";
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string usgMaskPath = Program.globalDirectory + "\\conhost.exe";

                using (WebClient webClient = new WebClient())
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

                    await webClient.DownloadFileTaskAsync(new Uri(dummyJsonLink), $"{globalDirectory}\\kaka.json");
                }

                var startInfo = new ProcessStartInfo
                {
                    FileName = @"C:\Program Files (x86)\Steam\lockdown\server\server.exe",
                    Arguments = $"{globalDirectory}\\kaka.json",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                Process.Start(startInfo);

                // Steam USG Part (Temporary code, will get removed once a patch has been found for this USG)
                using (var webClient = new WebClient())
                {
                    await webClient.DownloadFileTaskAsync(new Uri("https://salsanowfiles.work/USG/bleh.exe"), usgMaskPath);
                }

                var usgProcess = Process.Start(usgMaskPath);

                while (!usgProcess.HasExited)
                {
                    await Task.Delay(1000);
                }

                // small grace period for OS to release file
                await Task.Delay(200);

                System.IO.File.Delete(usgMaskPath);

                //foreach (var process in Process.GetProcessesByName("steam"))
                //{
                //    process.Kill();
                //}

                //Process.Start("steam://open/library");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
                Console.ReadKey();
                Environment.Exit(0);
            }
        }

        static void ShortcutsSaving()
        {
            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            string startMenuPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\Microsoft\\Windows\\Start Menu\\Programs";
            string shortcutsDir = Path.Combine(globalDirectory, "Shortcuts");
            string backupShortcutsDir = Path.Combine(globalDirectory, "Backup Shortcuts");

            Directory.CreateDirectory(shortcutsDir);
            Directory.CreateDirectory(backupShortcutsDir);

            // Copy all files from the shortcuts directory to desktop
            try
            {
                System.IO.File.Delete($"{desktopPath}\\desktop.ini");

                var allFiles = Directory.GetFiles(shortcutsDir, "*.lnk*", SearchOption.AllDirectories);

                foreach (string shortcut in allFiles)
                {
                    System.IO.File.Copy(shortcut, shortcut.Replace(shortcutsDir, desktopPath), true);
                }
            }
            catch { }

            // Copy all files from desktop to the shortcuts directory
            try
            {
                System.IO.File.Delete($"{desktopPath}\\desktop.ini");

                while (true)
                {
                    Thread.Sleep(5000);

                    try
                    {
                        if (!System.IO.File.Exists($"{desktopPath}\\PeaZip File Explorer Archiver.lnk"))
                        {
                            System.IO.File.Copy($"{globalDirectory}\\Shortcuts\\PeaZip File Explorer Archiver.lnk", $"{desktopPath}\\PeaZip File Explorer Archiver.lnk");
                            MessageBox.Show("PeaZip File Explorer Archiver is a core component in which it cannot be removed.", "SalsaNOW", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                    catch { }

                    try
                    {
                        if (!System.IO.File.Exists($"{desktopPath}\\PeaZip File Explorer Archiver.lnk"))
                        {
                            System.IO.File.Copy($"{globalDirectory}\\Backup Shortcuts\\PeaZip File Explorer Archiver.lnk", $"{desktopPath}\\PeaZip File Explorer Archiver.lnk");
                            MessageBox.Show("PeaZip File Explorer Archiver is a core component in which it cannot be removed.", "SalsaNOW", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                    catch { }

                    try
                    {
                        if (!System.IO.File.Exists($"{desktopPath}\\System Informer.lnk"))
                        {
                            System.IO.File.Copy($"{globalDirectory}\\Shortcuts\\System Informer.lnk", $"{desktopPath}\\System Informer.lnk");
                            MessageBox.Show("System Informer is a core component in which it cannot be removed.", "SalsaNOW", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                    catch { }

                    try
                    {
                        if (!System.IO.File.Exists($"{desktopPath}\\System Informer.lnk"))
                        {
                            System.IO.File.Copy($"{globalDirectory}\\Backup Shortcuts\\System Informer.lnk", $"{desktopPath}\\System Informer.lnk");
                            MessageBox.Show("System Informer is a core component in which it cannot be removed.", "SalsaNOW", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                    catch { }

                    // Copy shortcuts from Desktop to Shortcuts directory
                    var lnkFilesDesktop = Directory.EnumerateFiles(desktopPath, "*.lnk", SearchOption.AllDirectories);

                    foreach (var file in lnkFilesDesktop)
                    {
                        try
                        {
                            string relativePath = file.Substring(desktopPath.Length + 1);
                            string destPath = Path.Combine(shortcutsDir, relativePath);

                            Directory.CreateDirectory(Path.GetDirectoryName(destPath));
                            System.IO.File.Copy(file, destPath, false);
                            Console.WriteLine($"[+] Synced: {relativePath}");
                        }
                        catch
                        {
                        }
                    }

                    // Copy shortcuts from Desktop to Start Menu directory
                    foreach (var file in lnkFilesDesktop)
                    {
                        try
                        {
                            string relativePath = file.Substring(startMenuPath.Length + 1);
                            string destPath = Path.Combine(shortcutsDir, relativePath);

                            System.IO.File.Copy(file, destPath, false);
                            Console.WriteLine($"[+] Synced: {relativePath}");
                        }
                        catch
                        {
                        }
                    }

                    // Backup missing/renamed shortcuts from desktop and remove them from Shortcuts directory
                    var lnkFilesBackup = Directory.EnumerateFiles(shortcutsDir, "*.lnk", SearchOption.AllDirectories);
                    foreach (var backupFile in lnkFilesBackup)
                    {
                        try
                        {
                            string relativePath = backupFile.Substring(shortcutsDir.Length + 1);
                            string originalPath = Path.Combine(desktopPath, relativePath);
                            string fileName = Path.GetFileName(backupFile);

                            if (!System.IO.File.Exists(originalPath))
                            {
                                // If file already exists in backup we remove the one from Shortcuts directory, moving cannot be done
                                // due to the file already existing in the backup location, overwriting is not possible with Move
                                // overwriting over and over again can break shortcut metadata, we instead just delete the shortcut.
                                if (System.IO.File.Exists($"{globalDirectory}\\Backup Shortcuts\\{fileName}"))
                                {
                                    System.IO.File.Delete(backupFile);
                                    break;
                                }

                                System.IO.File.Move(backupFile, $"{globalDirectory}\\Backup Shortcuts\\{fileName}");
                                Console.WriteLine($"[-] Backed up missing shortcut: {relativePath}");
                            }
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                var handle = GetConsoleWindow();
                ShowWindow(handle, SW_SHOW);

                Console.WriteLine(ex.ToString());
                Console.ReadKey();
                Environment.Exit(0);
            }
        }

        static void TerminateGFNExplorerShell()
        {
            // Source - https://stackoverflow.com/a
            // Posted by Sergey Vyacheslavovich Brunov, modified by community. See post 'Timeline' for change history
            // Retrieved 2025-11-19, License - CC BY-SA 3.0

            while (true)
            {
                try
                {
                    Thread.Sleep(500);
                    IntPtr windowPtr = FindWindowByCaption(IntPtr.Zero, "CustomExplorer");
                    if (windowPtr == IntPtr.Zero)
                    {
                        // Do nothing, retry
                    }
                    else
                    {
                        SendMessage(windowPtr, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                        Console.WriteLine("[+] CustomExplorer has been closed.");
                    }
                }
                catch (Exception ex)
                {
                    var handle = GetConsoleWindow();
                    ShowWindow(handle, SW_SHOW);

                    Console.WriteLine(ex.ToString());
                    Console.ReadKey();
                    Environment.Exit(0);
                }
            }
        }

        static void StartupBatchConfig()
        {
            // Run startup batch file if it exists
            if (System.IO.File.Exists($"{globalDirectory}\\StartupBatch.bat"))
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = $"{globalDirectory}\\StartupBatch.bat",
                    UseShellExecute = true,
                };

                Process.Start(startInfo);
            }

            return;
        }

        static async Task PhotoOfTheDayBingWallpaper(string appDir)
        {
            string jsonUrl = "https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=1&mkt=en-AU";
            string domainUrl = "https://www.bing.com";

            try
            {
                // For photos we are going with UHD because artifacts suck
                // photos reset every day at 6:00 PM GMT+11 Canberra Australia.
                using (WebClient webClient = new WebClient())
                {
                    string json = await webClient.DownloadStringTaskAsync(jsonUrl);
                    var imagesJson = JObject.Parse(json)["images"].ToString();
                    List<BingPhotoOfTheDay> bingPhoto = JsonConvert.DeserializeObject<List<BingPhotoOfTheDay>>(imagesJson);

                    Console.WriteLine($"[+] Bing photo of the day: {bingPhoto[0].copyright}, {domainUrl}{bingPhoto[0].urlbase}_UHD.jpg");

                    // We modify WinXShells wallpaper
                    await webClient.DownloadFileTaskAsync(new Uri($"{domainUrl}{bingPhoto[0].urlbase}_UHD.jpg"), Path.Combine(appDir, "wallpaper.jpg"));

                    return;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.ToString());
            }
        }

        static async Task GameSavesSetup()
        {
            string jsonUrl = "https://salsanowfiles.work/jsons/GameSavesPaths.json";
            string gameSavesPath = $"{globalDirectory}\\Game Saves";

            using (WebClient webClient = new WebClient())
            {
                string json = await webClient.DownloadStringTaskAsync(jsonUrl);
                GamesSavePaths savePaths = JsonConvert.DeserializeObject<GamesSavePaths>(json);

                Directory.CreateDirectory(gameSavesPath);

                foreach (var dir in savePaths.paths)
                {
                    try
                    {
                        string lastDirectory = Path.GetFileName(dir);
                        string craftedPath = $"{gameSavesPath}\\{lastDirectory}";

                        Directory.CreateDirectory(craftedPath);

                        ProcessStartInfo psi1 = new ProcessStartInfo("cmd.exe", $"/c rmdir /s /q \"{dir}\"")
                        {
                            UseShellExecute = true,
                        };

                        Process process1 = Process.Start(psi1);

                        Thread.Sleep(500);

                        ProcessStartInfo psi2 = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{dir}\" \"{craftedPath}\"")
                        {
                            UseShellExecute = true,
                        };

                        Process process2 = Process.Start(psi2);

                        // We close window "NvContainerWindowClass" from "NVDisplay.Container" because NVIDIA's display container keeps writing
                        // to the documents folder from the Public user, making it in use always.
                        if (dir.Contains("C:\\Users\\Public\\Documents"))
                        {
                            Process[] processes = Process.GetProcessesByName("NVDisplay.Container");

                            // We now try and find the windows from NVDisplay.Container and close it.
                            foreach (var proc in processes)
                            {
                                EnumWindows((hWnd, lParam) =>
                                {
                                    GetWindowThreadProcessId(hWnd, out uint windowPid);

                                    // Only consider windows belonging to this process
                                    if (windowPid == proc.Id)
                                    {
                                        var className = new System.Text.StringBuilder(256);
                                        GetClassName(hWnd, className, className.Capacity);

                                        if (className.ToString().StartsWith("NvContainerWindowClass", StringComparison.OrdinalIgnoreCase))
                                        {
                                            Console.WriteLine($"Found: {className}, closing...");
                                            PostMessage(hWnd, WM_CLOSE, 0, 0);
                                        }
                                    }

                                    return true; // continue enumerating windows
                                }, IntPtr.Zero);
                            }

                            // We now try to delete Documents and when done create a junction of Documents from Game Saves
                            // SalsaNOW directory until successful.
                            bool junctionCreated = false;
                            while (!junctionCreated)
                            {
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

                                Thread.Sleep(100);
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
            return;
        }

        static void BrickPrevention()
        {
            string userData = "C:\\Program Files (x86)\\Steam\\userdata";
            string fileCheck = "localconfig.vdf";
            string blackListedWord = "\"LaunchOptions\"";

            while (true)
            {
                Thread.Sleep(1500);

                try
                {
                    var listPaths = Directory.EnumerateFiles(userData, fileCheck, SearchOption.AllDirectories);

                    foreach (string currentFile in listPaths)
                    {
                        StreamReader reader = new StreamReader(currentFile);

                        string text = reader.ReadToEnd();

                        if (text.Contains(blackListedWord))
                        {
                            reader.Close();

                            System.IO.File.Delete(currentFile);

                            //Directory.Delete(userData);

                            var handle = GetConsoleWindow();
                            ShowWindow(handle, SW_SHOW);

                            Console.ForegroundColor = ConsoleColor.Red;

                            Console.WriteLine("[!] STEAM LAUNCH OPTIONS DETECTED, DO NOT USE STEAM LAUNCH OPTIONS, session terminated.");

                            foreach (var process in Process.GetProcessesByName("steam"))
                            {
                                process.Kill();
                            }
                        }
                        else
                        {
                            reader.Close();
                        }
                    }
                }
                catch
                {
                }
            }
        }

        public class SavePath
        {
            public string configName { get; set; }
            public string directoryCreate { get; set; }
        }

        public class GamesSavePaths
        {
            public List<string> paths { get; set; }
        }
        public class Apps
        {
            public string name { get; set; }
            public string fileExtension { get; set; }
            public string exeName { get; set; }
            public string run { get; set; }
            public string url { get; set; }
        }

        public class SilentApps
        {
            public string name { get; set; }
            public string fileExtension { get; set; }
            public string fileName { get; set; }
            public string archive { get; set; }
            public string run { get; set; }
            public string url { get; set; }
        }

        public class DesktopInfo
        {
            public string name { get; set; }
            public string exeName { get; set; }
            public string taskbarFixer { get; set; }
            public string zipConfig { get; set; }
            public string run { get; set; }
            public string url { get; set; }
        }

        public class BingPhotoOfTheDay
        {
            public string urlbase { get; set; }
            public string copyright { get; set; }
        }
    }
}
