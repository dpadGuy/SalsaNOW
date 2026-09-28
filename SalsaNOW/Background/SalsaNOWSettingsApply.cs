using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SalsaNOW
{
    internal static class SalsaNOWSettingsApply
    {
        private const string ConfigPath = @"I:\Apps\SalsaNOW\SalsaNOWSettings.json";
        private const string WaterfoxPath = @"I:\Apps\SalsaNOW\Waterfox\waterfox.exe";
        private static readonly TimeSpan LaunchCooldown = TimeSpan.FromSeconds(3);

        private static readonly Regex UrlRegex = new Regex(
            @"https?://[^\s'""]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex NoiseUrlRegex = new Regex(
            @"edge://|microsoft\.com",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Dictionary<string, DateTime> RecentLaunches =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        private static DateTime _appliedConfigWriteTime = DateTime.MinValue;
        private static string _lastConfigError;

        public static bool TryReadHostFlags(out bool steamSilentLaunch, out bool bingWallpaper)
        {
            steamSilentLaunch = false;
            bingWallpaper = false;
            if (!File.Exists(ConfigPath))
                return false;

            SalsaNowSettingsFile settings;
            DateTime writeTime;
            if (!TryReadSettings(out settings, out writeTime))
                return false;

            steamSilentLaunch = settings.SteamSilentLaunch == true;
            bingWallpaper = settings.BingWallpaper == true;
            return true;
        }

        public static void Start(CancellationToken token)
        {
            StartEdgeInterceptor(token);
            ApplyBuiltInDefaults();

            SalsaNowSettingsFile settings;
            DateTime writeTime;
            if (File.Exists(ConfigPath) && TryReadSettings(out settings, out writeTime))
            {
                ApplyStartupSettings(settings, token);
                _appliedConfigWriteTime = writeTime;
            }
            else
            {
                _ = Task.Run(() => FinishDefaultAppearanceAsync(token));
            }

            _ = Task.Run(() => RefreshShellUntilReadyAsync(token));
            _ = Task.Run(() => WatchConfigAsync(token));
        }

        private static void ApplyBuiltInDefaults()
        {
            WriteDarkTheme();
            WriteAdvanced("TaskbarAl", 0);
            SalsaLogger.Info("Default dark theme and left taskbar alignment applied.");
        }

        private static void WriteDarkTheme()
        {
            WriteDword(PersonalizeKey, "SystemUsesLightTheme", 0);
            WriteDword(PersonalizeKey, "AppsUseLightTheme", 0);
            WriteDword(PersonalizeKey, "ColorPrevalence", 0);
            WriteDword(DwmKey, "ColorPrevalence", 0);
            WriteDword(DwmKey, "EnableWindowColorization", 0);
        }

        public static void RefreshShellAppearance()
        {
            SalsaNowSettingsFile settings;
            DateTime writeTime;
            if (File.Exists(ConfigPath) && TryReadSettings(out settings, out writeTime))
            {
                WriteThemeMode(settings);
                if (string.IsNullOrWhiteSpace(settings.ColorMode))
                    WriteDarkTheme();

                if (string.IsNullOrWhiteSpace(settings.TaskbarAlignment))
                    WriteAdvanced("TaskbarAl", 0);
            }
            else
            {
                WriteDarkTheme();
                WriteAdvanced("TaskbarAl", 0);
            }

            NotifyTheme();
            NotifyTraySettings();
        }

        private static async Task FinishDefaultAppearanceAsync(CancellationToken token)
        {
            try
            {
                if (!await WaitForExplorerDesktopAsync(token))
                    return;

                RefreshShellAppearance();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("Default appearance failed: " + ex.Message);
            }
        }

        private static async Task RefreshShellUntilReadyAsync(CancellationToken token)
        {
            try
            {
                if (!await WaitForExplorerDesktopAsync(token))
                    return;

                for (int attempt = 0; attempt < 4 && !token.IsCancellationRequested; attempt++)
                {
                    RefreshShellAppearance();
                    await Task.Delay(2000, token);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("Taskbar theme refresh failed: " + ex.Message);
            }
        }

        private static async Task WatchConfigAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(ConfigPath))
                    {
                        DateTime writeTime = File.GetLastWriteTimeUtc(ConfigPath);
                        SalsaNowSettingsFile settings;
                        if (writeTime != _appliedConfigWriteTime && TryReadSettings(out settings, out writeTime))
                        {
                            SalsaLogger.Info("SalsaNOWSettings.json changed. Applying file associations.");
                            ApplyAssociations(settings);
                            _appliedConfigWriteTime = writeTime;
                        }
                    }
                }
                catch
                {
                }

                try
                {
                    await Task.Delay(1000, token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }

        private static bool TryReadSettings(out SalsaNowSettingsFile settings, out DateTime writeTime)
        {
            settings = null;
            writeTime = DateTime.MinValue;

            try
            {
                writeTime = File.GetLastWriteTimeUtc(ConfigPath);
                settings = JsonConvert.DeserializeObject<SalsaNowSettingsFile>(File.ReadAllText(ConfigPath));
                if (settings == null)
                    return false;

                _lastConfigError = null;
                return true;
            }
            catch (Exception ex)
            {
                if (_lastConfigError != ex.Message)
                {
                    _lastConfigError = ex.Message;
                    SalsaLogger.Error("SalsaNOWSettings.json failed: " + ex.Message);
                }

                return false;
            }
        }

        private static void ApplyStartupSettings(SalsaNowSettingsFile settings, CancellationToken token)
        {
            SalsaLogger.Info("Applying SalsaNOWSettings.json.");
            ApplyColors(settings);
            ApplyTaskbar(settings);
            bool startTranslucentTb = PrepareTranslucentTb(settings);
            ApplyAssociations(settings);

            if (IsExplorerDesktopReady())
            {
                RefreshTheme(settings);
                ApplyTaskbar(settings);
                StartTranslucentTb(startTranslucentTb);
            }
            else
            {
                _ = Task.Run(() => FinishDesktopAppearanceAsync(settings, startTranslucentTb, token));
            }
        }

        private static async Task FinishDesktopAppearanceAsync(SalsaNowSettingsFile settings, bool startTranslucentTb, CancellationToken token)
        {
            try
            {
                if (!await WaitForExplorerDesktopAsync(token))
                    return;

                RefreshTheme(settings);
                ApplyTaskbar(settings);
                StartTranslucentTb(startTranslucentTb);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("SalsaNOWSettings.json desktop appearance failed: " + ex.Message);
            }
        }

        private static async Task<bool> WaitForExplorerDesktopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                if (IsExplorerDesktopReady())
                {
                    try
                    {
                        await Task.Delay(1000, token);
                    }
                    catch (TaskCanceledException)
                    {
                        return false;
                    }

                    if (IsExplorerDesktopReady())
                        return true;
                }

                try
                {
                    await Task.Delay(500, token);
                }
                catch (TaskCanceledException)
                {
                    return false;
                }
            }

            return false;
        }

        private static void ApplyColors(SalsaNowSettingsFile settings)
        {
            try
            {
                WriteThemeMode(settings);
                WriteAccentSetting(settings);
                SalsaLogger.Info("SalsaNOWSettings.json colors applied.");
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("SalsaNOWSettings.json colors failed: " + ex.Message);
            }
        }

        private static void RefreshTheme(SalsaNowSettingsFile settings)
        {
            try
            {
                WriteThemeMode(settings);
                NotifyTheme();
                SalsaLogger.Info("SalsaNOWSettings.json light or dark theme refreshed.");
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("SalsaNOWSettings.json theme refresh failed: " + ex.Message);
            }
        }

        private static void WriteThemeMode(SalsaNowSettingsFile settings)
        {
            if (string.Equals(settings.ColorMode, "custom", StringComparison.OrdinalIgnoreCase))
            {
                WriteDword(PersonalizeKey, "SystemUsesLightTheme", IsLight(settings.WindowsMode) ? 1 : 0);
                WriteDword(PersonalizeKey, "AppsUseLightTheme", IsLight(settings.AppsMode) ? 1 : 0);
            }
            else if (!string.IsNullOrWhiteSpace(settings.ColorMode))
            {
                int light = IsLight(settings.ColorMode) ? 1 : 0;
                WriteDword(PersonalizeKey, "SystemUsesLightTheme", light);
                WriteDword(PersonalizeKey, "AppsUseLightTheme", light);
            }

            if (settings.Transparency.HasValue)
                WriteDword(PersonalizeKey, "EnableTransparency", settings.Transparency.Value ? 1 : 0);

            WriteDword(PersonalizeKey, "ColorPrevalence", 0);
            WriteDword(DwmKey, "ColorPrevalence", 0);
            WriteDword(DwmKey, "EnableWindowColorization", 0);
        }

        private static void WriteAccentSetting(SalsaNowSettingsFile settings)
        {
            if (settings.AccentSourceAutomatic)
            {
                WriteDword(DesktopKey, "AutoColorization", 1);
                return;
            }

            WriteDword(DesktopKey, "AutoColorization", 0);
            int accent;
            if (TryParseAccent(settings.AccentColor, out accent))
                ApplyAccentColor(accent);
        }

        private static void NotifyTheme()
        {
            IntPtr result;
            IntPtr broadcast = new IntPtr(0xFFFF);
            SendNotifyMessage(broadcast, 0x001A, IntPtr.Zero, "ImmersiveColorSet");
            SendMessageTimeout(broadcast, 0x001A, IntPtr.Zero, "ImmersiveColorSet", 0x0002, 500, out result);

            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero)
            {
                SendNotifyMessage(tray, 0x001A, IntPtr.Zero, "ImmersiveColorSet");
                SendMessageTimeout(tray, 0x001A, IntPtr.Zero, "ImmersiveColorSet", 0x0002, 500, out result);
            }

            IntPtr progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                SendNotifyMessage(progman, 0x001A, IntPtr.Zero, "ImmersiveColorSet");
                SendMessageTimeout(progman, 0x001A, IntPtr.Zero, "ImmersiveColorSet", 0x0002, 500, out result);
            }
        }

        private static void ApplyTaskbar(SalsaNowSettingsFile settings)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(settings.TaskbarAlignment))
                    WriteAdvanced("TaskbarAl", string.Equals(settings.TaskbarAlignment, "left", StringComparison.OrdinalIgnoreCase) ? 0 : 1);

                if (settings.TaskbarBadges.HasValue)
                    WriteAdvanced("TaskbarBadges", settings.TaskbarBadges.Value ? 1 : 0);

                if (settings.TaskbarFlashing.HasValue)
                    WriteAdvanced("TaskbarFlashing", settings.TaskbarFlashing.Value ? 1 : 0);

                if (settings.TaskbarShareWindows.HasValue)
                    WriteAdvanced("TaskbarSn", settings.TaskbarShareWindows.Value ? 1 : 0);

                if (settings.TaskbarShowDesktopCorner.HasValue)
                    WriteAdvanced("TaskbarSd", settings.TaskbarShowDesktopCorner.Value ? 1 : 0);

                if (!string.IsNullOrWhiteSpace(settings.TaskbarCombine))
                    WriteAdvanced("TaskbarGlomLevel", CombineValue(settings.TaskbarCombine));

                if (!string.IsNullOrWhiteSpace(settings.TaskbarSmallerButtons))
                {
                    int preference = SmallerValue(settings.TaskbarSmallerButtons);
                    WriteAdvanced("IconSizePreference", preference);
                    if (preference == 0)
                        WriteAdvanced("TaskbarSi", 0);
                }

                if (settings.TaskbarAutoHide.HasValue)
                    SetAutoHide(settings.TaskbarAutoHide.Value);

                NotifyTraySettings();
                SalsaLogger.Info("SalsaNOWSettings.json taskbar applied.");
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("SalsaNOWSettings.json taskbar failed: " + ex.Message);
            }
        }

        private static bool PrepareTranslucentTb(SalsaNowSettingsFile settings)
        {
            if (!settings.TaskbarFullTransparency.HasValue)
                return false;

            try
            {
                if (!settings.TaskbarFullTransparency.Value)
                {
                    StopTranslucentTb();
                    return false;
                }

                Directory.CreateDirectory(TranslucentTbFolder);
                File.WriteAllText(TranslucentTbSettings, TranslucentTbClearSettings);
                if (!File.Exists(TranslucentTbExe))
                {
                    SalsaLogger.Warn("TranslucentTB.exe not found: " + TranslucentTbExe);
                    return false;
                }

                StopTranslucentTb();
                return true;
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("SalsaNOWSettings.json TranslucentTB failed: " + ex.Message);
                return false;
            }
        }

        private static void StartTranslucentTb(bool start)
        {
            if (!start || !IsExplorerDesktopReady() || !File.Exists(TranslucentTbExe))
                return;

            try
            {
                StopTranslucentTb();
                Process.Start(new ProcessStartInfo
                {
                    FileName = TranslucentTbExe,
                    WorkingDirectory = TranslucentTbFolder,
                    UseShellExecute = true
                });
                SalsaLogger.Info("SalsaNOWSettings.json TranslucentTB started.");
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("SalsaNOWSettings.json TranslucentTB failed: " + ex.Message);
            }
        }

        private static bool IsExplorerDesktopReady()
        {
            bool explorerRunning = false;
            foreach (Process process in Process.GetProcessesByName("explorer"))
            {
                try
                {
                    if (!process.HasExited)
                        explorerRunning = true;
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }

            if (!explorerRunning)
                return false;

            if (FindWindow("Progman", null) == IntPtr.Zero)
                return false;

            return FindWindow("Shell_TrayWnd", null) != IntPtr.Zero;
        }

        private static void ApplyAssociations(SalsaNowSettingsFile settings)
        {
            if (settings.Associations == null || settings.Associations.Count == 0)
                return;

            string setUserFta = settings.SetUserFta;
            if (string.IsNullOrWhiteSpace(setUserFta) || !File.Exists(setUserFta))
            {
                SalsaLogger.Warn("SetUserFTA.exe from SalsaNOWSettings.json was not found. Skipping configured associations.");
                return;
            }

            var associations = new List<KeyValuePair<string, string>>();
            foreach (SalsaNowAssociation item in settings.Associations)
            {
                if (string.IsNullOrWhiteSpace(item.Type) ||
                    string.IsNullOrWhiteSpace(item.ProgId) ||
                    string.IsNullOrWhiteSpace(item.Application))
                {
                    continue;
                }

                RegisterOpenCommand(@"Software\Classes\" + item.ProgId + @"\shell\open\command", item.Application);
                associations.Add(new KeyValuePair<string, string>(item.Type, item.ProgId));
            }

            ApplyUserFta(setUserFta, associations);
            SalsaLogger.Info("SalsaNOWSettings.json associations applied.");
        }

        private static void StartEdgeInterceptor(CancellationToken token)
        {
            _ = Task.Run(() => WatchEdgeAsync(token));
        }

        private static async Task WatchEdgeAsync(CancellationToken token)
        {
            string browserPath = null;
            DateTime configWriteTime = DateTime.MinValue;
            browserPath = RefreshDefaultBrowser(browserPath, ref configWriteTime);
            LogDefaultBrowser(browserPath, false);

            while (!token.IsCancellationRequested)
            {
                try
                {
                    string updated = RefreshDefaultBrowser(browserPath, ref configWriteTime);
                    if (!string.Equals(updated, browserPath, StringComparison.OrdinalIgnoreCase))
                    {
                        browserPath = updated;
                        LogDefaultBrowser(browserPath, true);
                    }

                    if (IsUsableBrowser(browserPath) && IsProcessRunning("msedge"))
                        RedirectEdgeUrls(browserPath);
                }
                catch
                {
                }

                try
                {
                    await Task.Delay(1000, token);
                }
                catch (TaskCanceledException)
                {
                    return;
                }
            }
        }

        private static string RefreshDefaultBrowser(string currentPath, ref DateTime configWriteTime)
        {
            if (!File.Exists(ConfigPath))
            {
                configWriteTime = DateTime.MinValue;
                return WaterfoxPath;
            }

            DateTime writeTime;
            try
            {
                writeTime = File.GetLastWriteTimeUtc(ConfigPath);
            }
            catch
            {
                return string.IsNullOrWhiteSpace(currentPath) ? WaterfoxPath : currentPath;
            }

            if (writeTime == configWriteTime && currentPath != null)
                return currentPath;

            try
            {
                SalsaNowSettingsFile settings = JsonConvert.DeserializeObject<SalsaNowSettingsFile>(File.ReadAllText(ConfigPath));
                configWriteTime = writeTime;
                if (settings == null || string.IsNullOrWhiteSpace(settings.DefaultBrowser))
                    return WaterfoxPath;

                return settings.DefaultBrowser;
            }
            catch
            {
                return string.IsNullOrWhiteSpace(currentPath) ? WaterfoxPath : currentPath;
            }
        }

        private static bool IsUsableBrowser(string browserPath)
        {
            return !string.IsNullOrWhiteSpace(browserPath)
                && !browserPath.EndsWith("msedge.exe", StringComparison.OrdinalIgnoreCase)
                && File.Exists(browserPath);
        }

        private static void LogDefaultBrowser(string browserPath, bool changed)
        {
            if (IsUsableBrowser(browserPath))
            {
                SalsaLogger.Info((changed ? "Default browser changed: " : "Edge interceptor running. Default browser: ") + browserPath);
                return;
            }

            if (string.IsNullOrWhiteSpace(browserPath))
                SalsaLogger.Warn(changed ? "Default browser was cleared. Edge URLs will not be redirected." : "Default browser is not set. Edge URLs will not be redirected.");
            else if (browserPath.EndsWith("msedge.exe", StringComparison.OrdinalIgnoreCase))
                SalsaLogger.Warn("Default browser is Edge. Edge URLs will not be redirected.");
            else
                SalsaLogger.Warn("Default browser not found: " + browserPath);
        }

        private static void RedirectEdgeUrls(string browserPath)
        {
            DateTime now = DateTime.UtcNow;
            PruneRecentLaunches(now);

            var pending = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            using (var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name = 'msedge.exe'"))
            using (ManagementObjectCollection processes = searcher.Get())
            {
                foreach (ManagementObject edge in processes)
                {
                    using (edge)
                    {
                        string cmdLine = edge["CommandLine"] as string;
                        string targetUrl = ExtractTargetUrl(cmdLine);
                        if (targetUrl == null)
                            continue;

                        int processId = Convert.ToInt32(edge["ProcessId"]);
                        if (!pending.TryGetValue(targetUrl, out List<int> processIds))
                        {
                            processIds = new List<int>();
                            pending[targetUrl] = processIds;
                        }

                        processIds.Add(processId);
                    }
                }
            }

            foreach (KeyValuePair<string, List<int>> item in pending)
            {
                foreach (int processId in item.Value)
                    KillProcess(processId);

                if (RecentLaunches.TryGetValue(item.Key, out DateTime launched) && now - launched < LaunchCooldown)
                    continue;

                RecentLaunches[item.Key] = now;
                SalsaLogger.Info("Intercepted Edge URL: " + item.Key);
                LaunchBrowser(browserPath, item.Key);
            }
        }

        private static string ExtractTargetUrl(string cmdLine)
        {
            if (string.IsNullOrEmpty(cmdLine))
                return null;

            foreach (Match match in UrlRegex.Matches(cmdLine))
            {
                string url = match.Value;
                if (NoiseUrlRegex.IsMatch(url))
                    continue;

                return url;
            }

            return null;
        }

        private static void KillProcess(int processId)
        {
            try
            {
                using (Process process = Process.GetProcessById(processId))
                {
                    if (!process.HasExited)
                        process.Kill();
                }
            }
            catch
            {
            }
        }

        private static void LaunchBrowser(string browserPath, string targetUrl)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = browserPath,
                    Arguments = "\"" + targetUrl + "\"",
                    UseShellExecute = false
                });
                SalsaLogger.Info("Launched default browser with URL.");
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("Failed to launch default browser: " + ex.Message);
            }
        }

        private static void PruneRecentLaunches(DateTime now)
        {
            if (RecentLaunches.Count == 0)
                return;

            var expired = new List<string>();
            foreach (KeyValuePair<string, DateTime> item in RecentLaunches)
            {
                if (now - item.Value > TimeSpan.FromSeconds(30))
                    expired.Add(item.Key);
            }

            foreach (string url in expired)
                RecentLaunches.Remove(url);
        }

        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const string DwmKey = @"Software\Microsoft\Windows\DWM";
        private const string AccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
        private const string DesktopKey = @"Control Panel\Desktop";
        private const string AdvancedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
        private const string StuckRectsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3";
        private const string TranslucentTbFolder = @"I:\Apps\SalsaNOW\Zxplorer\TranslucentTB";
        private const string TranslucentTbExe = @"I:\Apps\SalsaNOW\Zxplorer\TranslucentTB\TranslucentTB.exe";
        private const string TranslucentTbSettings = @"I:\Apps\SalsaNOW\Zxplorer\TranslucentTB\settings.json";
        private const string TranslucentTbClearSettings =
            "{\r\n" +
            "  \"$schema\": \"https://translucenttb.github.io/settings.schema.json\",\r\n" +
            "  \"desktop_appearance\": {\r\n" +
            "    \"accent\": \"clear\",\r\n" +
            "    \"color\": \"#00000000\",\r\n" +
            "    \"show_line\": false\r\n" +
            "  },\r\n" +
            "  \"visible_window_appearance\": {\r\n" +
            "    \"enabled\": false\r\n" +
            "  },\r\n" +
            "  \"maximized_window_appearance\": {\r\n" +
            "    \"enabled\": false\r\n" +
            "  },\r\n" +
            "  \"start_opened_appearance\": {\r\n" +
            "    \"enabled\": false\r\n" +
            "  },\r\n" +
            "  \"search_opened_appearance\": {\r\n" +
            "    \"enabled\": false\r\n" +
            "  },\r\n" +
            "  \"task_view_opened_appearance\": {\r\n" +
            "    \"enabled\": false\r\n" +
            "  },\r\n" +
            "  \"battery_saver_appearance\": {\r\n" +
            "    \"enabled\": false\r\n" +
            "  },\r\n" +
            "  \"hide_tray\": true,\r\n" +
            "  \"disable_saving\": true,\r\n" +
            "  \"verbosity\": \"off\",\r\n" +
            "  \"use_xaml_context_menu\": false\r\n" +
            "}\r\n";

        private static bool IsLight(string value) =>
            string.Equals(value, "light", StringComparison.OrdinalIgnoreCase);

        private static int CombineValue(string value)
        {
            if (string.Equals(value, "whenFull", StringComparison.OrdinalIgnoreCase))
                return 1;
            if (string.Equals(value, "never", StringComparison.OrdinalIgnoreCase))
                return 2;
            return 0;
        }

        private static int SmallerValue(string value)
        {
            if (string.Equals(value, "always", StringComparison.OrdinalIgnoreCase))
                return 0;
            if (string.Equals(value, "whenFull", StringComparison.OrdinalIgnoreCase))
                return 2;
            return 1;
        }

        private static bool TryParseAccent(string value, out int abgr)
        {
            abgr = 0;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            value = value.Trim().TrimStart('#');
            if (value.Length != 6)
                return false;

            int rgb;
            if (!int.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
                return false;

            byte red = (byte)((rgb >> 16) & 0xFF);
            byte green = (byte)((rgb >> 8) & 0xFF);
            byte blue = (byte)(rgb & 0xFF);
            abgr = unchecked((int)(0xFF000000u | ((uint)blue << 16) | ((uint)green << 8) | red));
            return true;
        }

        private static void ApplyAccentColor(int abgr)
        {
            WriteDword(DesktopKey, "AutoColorization", 0);
            WriteDword(AccentKey, "AccentColor", abgr);
            WriteDword(AccentKey, "AccentColorMenu", abgr);
            WriteBinary(AccentKey, "AccentPalette", BuildPalette(abgr));
        }

        private static byte[] BuildPalette(int abgr)
        {
            byte red = (byte)(abgr & 0xFF);
            byte green = (byte)((abgr >> 8) & 0xFF);
            byte blue = (byte)((abgr >> 16) & 0xFF);
            var bytes = new byte[32];
            for (int i = 0; i < 8; i++)
            {
                bytes[i * 4] = red;
                bytes[i * 4 + 1] = green;
                bytes[i * 4 + 2] = blue;
                bytes[i * 4 + 3] = 0xFF;
            }

            return bytes;
        }

        private static void SetAutoHide(bool hide)
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero)
            {
                var data = new AppBarData
                {
                    cbSize = Marshal.SizeOf(typeof(AppBarData)),
                    hWnd = tray
                };
                uint current = SHAppBarMessage(4, ref data);
                data.lParam = new IntPtr((int)((current & 2) | (hide ? 1u : 0u)));
                SHAppBarMessage(10, ref data);
            }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StuckRectsKey, true))
                {
                    byte[] settings = key != null ? key.GetValue("Settings") as byte[] : null;
                    if (settings == null || settings.Length < 9)
                        return;

                    settings[8] = hide
                        ? (byte)(settings[8] | 0x01)
                        : (byte)(settings[8] & ~0x01);
                    key.SetValue("Settings", settings, RegistryValueKind.Binary);
                }
            }
            catch
            {
            }
        }

        private static void StopTranslucentTb()
        {
            foreach (Process process in Process.GetProcessesByName("TranslucentTB"))
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(3000);
                }
                catch
                {
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        private static void WriteAdvanced(string name, int value) =>
            WriteDword(AdvancedKey, name, value);

        private static void WriteDword(string keyPath, string name, int value)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                key?.SetValue(name, value, RegistryValueKind.DWord);
            }
        }

        private static void WriteBinary(string keyPath, string name, byte[] value)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                key?.SetValue(name, value, RegistryValueKind.Binary);
            }
        }

        private static bool IsProcessRunning(string processName)
        {
            Process[] processes = Process.GetProcessesByName(processName);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                for (int i = 0; i < processes.Length; i++)
                    processes[i].Dispose();
            }
        }

        private static void NotifyTraySettings()
        {
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero)
                SendNotifyMessage(tray, 0x001A, IntPtr.Zero, "TraySettings");

            SendNotifyMessage(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "TraySettings");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AppBarData
        {
            public int cbSize;
            public IntPtr hWnd;
            public uint uCallbackMessage;
            public uint uEdge;
            public int left;
            public int top;
            public int right;
            public int bottom;
            public IntPtr lParam;
        }

        [DllImport("shell32.dll")]
        private static extern uint SHAppBarMessage(uint message, ref AppBarData data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string className, string windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd,
            uint msg,
            IntPtr wParam,
            string lParam,
            uint flags,
            uint timeout,
            out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SendNotifyMessage(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

        private static void RegisterOpenCommand(string keyPath, string exePath)
        {
            if (!File.Exists(exePath))
            {
                SalsaLogger.Warn("Skipping file association command, missing: " + exePath);
                return;
            }

            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                if (key == null)
                    return;

                key.SetValue("", "\"" + exePath + "\" \"%1\"", RegistryValueKind.String);
            }
        }

        internal static void ApplyUserFta(string setUserFta, List<KeyValuePair<string, string>> associations)
        {
            if (associations == null || associations.Count == 0)
                return;

            if (TryApplyUserFtaBatch(setUserFta, associations))
                return;

            foreach (KeyValuePair<string, string> item in associations)
                SetUserFta(setUserFta, item.Key, item.Value);
        }

        private static bool TryApplyUserFtaBatch(string setUserFta, List<KeyValuePair<string, string>> associations)
        {
            string configFile = Path.Combine(Path.GetTempPath(), "SalsaNOW-fta-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                var lines = new string[associations.Count];
                for (int i = 0; i < associations.Count; i++)
                    lines[i] = associations[i].Key + ", " + associations[i].Value;

                File.WriteAllLines(configFile, lines);
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = setUserFta,
                    Arguments = "\"" + configFile + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    if (process == null)
                        return false;

                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                SalsaLogger.Warn("SetUserFTA batch failed, applying one at a time: " + ex.Message);
                return false;
            }
            finally
            {
                try
                {
                    if (File.Exists(configFile))
                        File.Delete(configFile);
                }
                catch { }
            }
        }

        private static void SetUserFta(string setUserFta, string extension, string progId)
        {
            try
            {
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = setUserFta,
                    Arguments = extension + " " + progId,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                }))
                {
                    process?.WaitForExit();
                }
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("SetUserFTA " + extension + " failed: " + ex.Message);
            }
        }

        private sealed class SalsaNowSettingsFile
        {
            [JsonProperty("setUserFta")]
            public string SetUserFta { get; set; }

            [JsonProperty("defaultBrowser")]
            public string DefaultBrowser { get; set; }

            [JsonProperty("associations")]
            public List<SalsaNowAssociation> Associations { get; set; }

            [JsonProperty("colorMode")]
            public string ColorMode { get; set; }

            [JsonProperty("windowsMode")]
            public string WindowsMode { get; set; }

            [JsonProperty("appsMode")]
            public string AppsMode { get; set; }

            [JsonProperty("transparency")]
            public bool? Transparency { get; set; }

            [JsonProperty("accentSourceAutomatic")]
            public bool AccentSourceAutomatic { get; set; }

            [JsonProperty("accentColor")]
            public string AccentColor { get; set; }

            [JsonProperty("taskbarAlignment")]
            public string TaskbarAlignment { get; set; }

            [JsonProperty("taskbarAutoHide")]
            public bool? TaskbarAutoHide { get; set; }

            [JsonProperty("taskbarBadges")]
            public bool? TaskbarBadges { get; set; }

            [JsonProperty("taskbarFlashing")]
            public bool? TaskbarFlashing { get; set; }

            [JsonProperty("taskbarShareWindows")]
            public bool? TaskbarShareWindows { get; set; }

            [JsonProperty("taskbarShowDesktopCorner")]
            public bool? TaskbarShowDesktopCorner { get; set; }

            [JsonProperty("taskbarCombine")]
            public string TaskbarCombine { get; set; }

            [JsonProperty("taskbarSmallerButtons")]
            public string TaskbarSmallerButtons { get; set; }

            [JsonProperty("taskbarFullTransparency")]
            public bool? TaskbarFullTransparency { get; set; }

            [JsonProperty("steamSilentLaunch")]
            public bool? SteamSilentLaunch { get; set; }

            [JsonProperty("bingWallpaper")]
            public bool? BingWallpaper { get; set; }
        }

        private sealed class SalsaNowAssociation
        {
            [JsonProperty("type")]
            public string Type { get; set; }

            [JsonProperty("progId")]
            public string ProgId { get; set; }

            [JsonProperty("application")]
            public string Application { get; set; }
        }
    }
}
