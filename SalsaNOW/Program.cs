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
            Console.Title = "SalsaNOW - by dpadGuy";

            // Making sure no SSL/TLS issues occur
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls13;
            ServicePointManager.ServerCertificateValidationCallback += (sender, cert, chain, errors) => true;

            await Startup();
            await AppsInstall();
            await DesktopInstall();
            await SteamServerShutdown();
            await AltTabSolution();

            _ = Task.Run(() => TerminateGFNExplorerShell());

            // Hide console window
            var handle = GetConsoleWindow();
            ShowWindow(handle, SW_HIDE);

            _ = Task.Run(async () => await GameSavesSetup());
            StartupBatchConfig();
            ShortcutsSaving();
        }

        static async Task Startup()
        {
            string jsonUrl = "https://pub-b8de31eeed5042ee8a9182cdf910ab07.r2.dev/jsons/directory.json";

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
                        await webClient.DownloadFileTaskAsync(new Uri("https://pub-b8de31eeed5042ee8a9182cdf910ab07.r2.dev/jsons/SalsaNOWConfig.ini"), $"{globalDirectory}\\SalsaNOWConfig.ini");
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
            string jsonUrl = "https://pub-b8de31eeed5042ee8a9182cdf910ab07.r2.dev/jsons/apps.json";
            string salsaNowIniPath = $"{globalDirectory}\\SalsaNOWConfig.ini";

            try
            {
                var salsaNowIniOpen = System.IO.File.ReadAllLines($"{globalDirectory}\\SalsaNOWConfig.ini");

                WebClient wc = new WebClient();
                string json = await wc.DownloadStringTaskAsync(jsonUrl);
                List<Apps> apps = JsonConvert.DeserializeObject<List<Apps>>(json);

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
        static async Task DesktopInstall()
        {
            string jsonUrl = "https://pub-b8de31eeed5042ee8a9182cdf910ab07.r2.dev/jsons/desktop.json";
            string salsaNowIniPath = $"{globalDirectory}\\SalsaNOWConfig.ini";

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
                string dummyJsonLink = "https://pub-b8de31eeed5042ee8a9182cdf910ab07.r2.dev/jsons/kaka.json";
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

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

                foreach (var process in Process.GetProcessesByName("steam"))
                {
                    process.Kill();
                }

                Process.Start("steam://open/library");
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
                        if (!System.IO.File.Exists($"{desktopPath}\\Explorer++.lnk"))
                        {
                            System.IO.File.Copy($"{globalDirectory}\\Shortcuts\\Explorer++.lnk", $"{desktopPath}\\Explorer++.lnk");
                            MessageBox.Show("Explorer++ is a core component in which it cannot be removed.", "SalsaNOW", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                    catch { }

                    try
                    {
                        if (!System.IO.File.Exists($"{desktopPath}\\Explorer++.lnk"))
                        {
                            System.IO.File.Copy($"{globalDirectory}\\Backup Shortcuts\\Explorer++.lnk", $"{desktopPath}\\Explorer++.lnk");
                            MessageBox.Show("Explorer++ is a core component in which it cannot be removed.", "SalsaNOW", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
        static async Task AltTabSolution()
        {
            string ctrlTabLink = "https://pub-b8de31eeed5042ee8a9182cdf910ab07.r2.dev/exes/ctrl_tab.exe";

            try
            {
                using (WebClient webClient = new WebClient())
                {
                    await webClient.DownloadFileTaskAsync(ctrlTabLink, $"{globalDirectory}\\ctrl_tab.exe");
                    Process.Start($"{globalDirectory}\\ctrl_tab.exe");
                    return;
                }
            }
            catch { }
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
            string jsonUrl = "https://pub-b8de31eeed5042ee8a9182cdf910ab07.r2.dev/jsons/GameSavesPaths.json";
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