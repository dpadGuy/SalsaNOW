using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SalsaNOW
{
    internal class RecoveryMode
    {
        private class MenuOption
        {
            public string Text { get; }
            public Func<Task> Action { get; }

            public MenuOption(string text, Func<Task> action)
            {
                Text = text;
                Action = action;
            }
        }

        public static async Task ShowRecoveryPrompt()
        {
            Console.Clear();

            var options = new[]
            {
                new MenuOption(
                    "Factory reset (This will remove the entire SalsaNOW folder and perform a fresh installation. Everything else on the I: drive will remain untouched.)",
                    FactoryReset),

                new MenuOption(
                    "Restore default shortcuts",
                    RestoreShortcuts),

                new MenuOption(
                    "Install Explorer++",
                    InstallExplorer),

                new MenuOption(
                    "Install System Informer",
                    InstallSystemInformer),

                new MenuOption(
                    "Exit",
                    Exit)
            };

            int selected = 0;

            // Resize console
            int requiredWidth = Math.Min(
                options.Max(o => o.Text.Length) + 6,
                Console.LargestWindowWidth);

            int requiredHeight = Math.Min(20, Console.LargestWindowHeight);

            if (Console.BufferWidth < requiredWidth)
                Console.SetBufferSize(requiredWidth, Console.BufferHeight);

            Console.SetWindowSize(requiredWidth, requiredHeight);

            while (true)
            {
                Console.Clear();
                Console.WriteLine("Select an option (use arrows to navigate):\n");

                for (int i = 0; i < options.Length; i++)
                {
                    if (i == selected)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"> {options[i].Text}");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.WriteLine($"  {options[i].Text}");
                    }
                }

                switch (Console.ReadKey(true).Key)
                {
                    case ConsoleKey.UpArrow:
                        selected = (selected - 1 + options.Length) % options.Length;
                        break;

                    case ConsoleKey.DownArrow:
                        selected = (selected + 1) % options.Length;
                        break;

                    case ConsoleKey.Enter:
                        Console.Clear();
                        await options[selected].Action();
                        continue;
                }
            }
        }

        static async Task FactoryReset()
        {
            Console.WriteLine("Factory Reset selected.");

            string globalDirectory = "";

            using (var wc = new WebClient())
            {
                var dir = JsonConvert.DeserializeObject<System.Collections.Generic.List<SavePath>>(await wc.DownloadStringTaskAsync("https://salsanowfiles.work/jsons/directory.json"))[0];
                globalDirectory = dir.directoryCreate;
            }

            if (!Directory.Exists(globalDirectory))
            {
                SalsaLogger.Error($"Global directory '{globalDirectory}' does not exist, make sure you first install SalsaNOW.");

                Thread.Sleep(3000);

                return;
            }

            // sanity check, dont want to accidentally nuke C:\
            // The directory comes from a remote JSON; never blindly recurse-delete it.
            if (string.IsNullOrWhiteSpace(globalDirectory) ||
                globalDirectory.TrimEnd('\\', '/').Length <= 3) // e.g. "C:\" root
            {
                SalsaLogger.Error("Refusing factory reset: resolved globalDirectory is null, empty, or a drive root ('" + globalDirectory + "').");
                Thread.Sleep(3000);
                return;
            }

            Directory.Delete(globalDirectory, true);

            Console.Clear();

            Process.Start("cmd.exe", $"/c start \"\" \"{Process.GetCurrentProcess().MainModule.FileName}\"");
            Environment.Exit(0);
        }

        static async Task RestoreShortcuts()
        {
            Console.WriteLine("Restore shortcuts selected.");

            const string jsonUrl = "https://salsanowfiles.work/jsons/apps.json";

            try
            {
                string globalDirectory = "";

                using (var wc = new WebClient())
                {
                    var dir = JsonConvert.DeserializeObject<List<SavePath>>(
                        await wc.DownloadStringTaskAsync("https://salsanowfiles.work/jsons/directory.json"))[0];

                    globalDirectory = dir.directoryCreate;

                    if(!Directory.Exists(globalDirectory))
                    {
                        SalsaLogger.Error($"Global directory '{globalDirectory}' does not exist, make sure you first install SalsaNOW.");

                        Thread.Sleep(3000);

                        return;
                    }

                    string json = await wc.DownloadStringTaskAsync(jsonUrl);
                    var apps = JsonConvert.DeserializeObject<List<Apps>>(json);

                    foreach (var app in apps)
                    {
                        string desktopPath = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                            $"{app.name}.lnk");

                        string targetPath;

                        if (app.fileExtension == "zip")
                        {
                            targetPath = Path.Combine(globalDirectory, app.name, app.exeName);
                        }
                        else
                        {
                            targetPath = Path.Combine(globalDirectory, app.exeName);
                        }

                        if (!File.Exists(targetPath))
                            continue;

                        AppInstaller.CreateShortcut(
                            app.name,
                            desktopPath,
                            targetPath,
                            Path.GetDirectoryName(targetPath));
                    }
                }

                Console.Clear();

                Process.Start("cmd.exe", $"/c start \"\" \"{Process.GetCurrentProcess().MainModule.FileName}\"");
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                SalsaLogger.Error(ex.ToString());
            }
        }

        static async Task InstallExplorer()
        {
            Console.WriteLine("Installing Explorer++...");

            try
            {
                string exeDirectory = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
                string destination = Path.Combine(exeDirectory, "Explorer++.exe");

                using (var wc = new WebClient())
                {
                    await wc.DownloadFileTaskAsync(
                        new Uri("https://salsanowfiles.work/exes/Explorer%2B%2B.exe"),
                        destination);
                }

                Process.Start(destination);

                SalsaLogger.Info("Explorer++ downloaded successfully.");
            }
            catch (Exception ex)
            {
                SalsaLogger.Error(ex.ToString());
            }
        }

        static async Task InstallSystemInformer()
        {
            Console.WriteLine("Installing System Informer...");

            try
            {
                string salsaExeDirectory = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
                string systemInformerDirectory = Path.Combine(salsaExeDirectory, "System Informer");
                string systemInformerExePath = Path.Combine(systemInformerDirectory, "SystemInformer.exe");

                string zipPath = Path.Combine(salsaExeDirectory, "System.Informer.zip");

                if(Directory.Exists(systemInformerDirectory))
                {
                    Directory.Delete(systemInformerDirectory, true);
                }

                using (var wc = new WebClient())
                {
                    await wc.DownloadFileTaskAsync(
                        new Uri("https://salsanowfiles.work/zips/System.Informer.zip"),
                        zipPath);
                }

                ZipFile.ExtractToDirectory(zipPath, systemInformerDirectory);

                File.Delete(zipPath);

                Process.Start(systemInformerExePath);
            }
            catch (Exception ex)
            {
                SalsaLogger.Error(ex.ToString());
            }
        }

        private static Task Exit()
        {
            Environment.Exit(0);
            return Task.CompletedTask; // Unreachable, but satisfies the compiler
        }
    }
}