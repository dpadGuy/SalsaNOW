using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace SalsaNOW
{
    internal static class DotNetInstaller
    {
        private static readonly string[] Channels = { "5.0", "6.0", "7.0", "8.0", "9.0", "10.0" };

        public static Task StartDotNetInstallAsync(CancellationToken token)
        {
            return Task.Run(() => RunAsync(token), token);
        }

        private static async Task RunAsync(CancellationToken token)
        {
            SalsaLogger.Info("Starting .NET runtime install...");

            try
            {
                string installDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft",
                    "dotnet");

                Directory.CreateDirectory(installDir);

                var pending = Channels.Where(channel => !IsDotnetVersionInstalled(channel, installDir)).ToArray();
                if (pending.Length == 0)
                {
                    SalsaLogger.Info("All required .NET runtimes are already installed.");
                    return;
                }

                var packs = (await Task.WhenAll(pending.Select(async channel =>
                {
                    token.ThrowIfCancellationRequested();

                    string version = await GetLatestRuntimeVersionAsync(channel);
                    if (string.IsNullOrEmpty(version))
                    {
                        SalsaLogger.Error($"Could not resolve latest .NET {channel} version.");
                        return Array.Empty<RuntimePack>();
                    }

                    return new[]
                    {
                        new RuntimePack(channel, version, "WindowsDesktop", "windowsdesktop-runtime-win-x64.zip", "WindowsDesktop", "windowsdesktop-runtime-{0}-win-x64.zip"),
                        new RuntimePack(channel, version, "ASP.NET", "aspnetcore-runtime-win-x64.zip", "aspnetcore/Runtime", "aspnetcore-runtime-{0}-win-x64.zip")
                    };
                }))).SelectMany(pack => pack).ToArray();

                if (packs.Length == 0)
                    return;

                SalsaLogger.Info("Downloading all .NET zips...");
                await Task.WhenAll(packs.Select(pack => DownloadPackAsync(pack, token)));

                var broken = packs.Where(pack => !IsIntactZip(pack.ZipPath)).Select(pack => pack.Label).ToArray();
                if (broken.Length > 0)
                {
                    SalsaLogger.Error("Not all .NET zips are intact. Skipping extract: " + string.Join(", ", broken));
                    CleanupZips(packs);
                    return;
                }

                SalsaLogger.Info("All .NET zips downloaded and intact. Extracting to " + installDir);

                foreach (RuntimePack pack in packs)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        ExtractZipEntries(pack.ZipPath, installDir);
                        SalsaLogger.Info("Extracted " + pack.Label);
                    }
                    catch (Exception ex)
                    {
                        SalsaLogger.Error("Failed to extract " + pack.Label + ": " + ex.Message);
                    }
                }

                CleanupZips(packs);

                var failed = Channels.Where(channel => !IsDotnetVersionInstalled(channel, installDir)).ToArray();
                if (failed.Length == 0)
                    SalsaLogger.Info("Successfully installed all .NET runtimes.");
                else
                    SalsaLogger.Error("DotNet install finished with missing runtimes: " + string.Join(", ", failed));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                SalsaLogger.Error("DotNet install failed: " + ex.Message);
            }
        }

        private static async Task<string> GetLatestRuntimeVersionAsync(string channel)
        {
            if (channel == "5.0")
                return "5.0.17";

            var endpoints = new[]
            {
                $"https://builds.dotnet.microsoft.com/dotnet/Runtime/{channel}/latest.version",
                $"https://dotnetcli.azureedge.net/dotnet/Runtime/{channel}/latest.version"
            };

            foreach (string endpoint in endpoints)
            {
                try
                {
                    using (var wc = new WebClient())
                    {
                        string version = ParseLatestVersion(await wc.DownloadStringTaskAsync(endpoint));
                        if (!string.IsNullOrEmpty(version))
                            return version;
                    }
                }
                catch { }
            }

            return null;
        }

        private static string ParseLatestVersion(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            foreach (string line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string value = line.Trim();
                if (value.Length > 0 && char.IsDigit(value[0]))
                    return value;
            }

            return null;
        }

        private static async Task DownloadPackAsync(RuntimePack pack, CancellationToken token)
        {
            try
            {
                SalsaLogger.Info("Downloading " + pack.Label + "...");
                pack.ZipPath = await DownloadFileAsync(pack.Urls, token);
            }
            catch (Exception ex)
            {
                SalsaLogger.Error("Download failed for " + pack.Label + ": " + ex.Message);
            }
        }

        private static async Task<string> DownloadFileAsync(List<string> urls, CancellationToken token)
        {
            string zipPath = Path.Combine(Path.GetTempPath(), "salsanow-dotnet-" + Guid.NewGuid().ToString("N") + ".zip");
            Exception lastError = null;

            foreach (string url in urls)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    using (var wc = new WebClient())
                    {
                        await wc.DownloadFileTaskAsync(new Uri(url), zipPath);
                    }

                    if (IsIntactZip(zipPath))
                        return zipPath;
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }

                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
            }

            throw lastError ?? new InvalidOperationException("Failed to download file from any endpoint.");
        }

        private static bool IsIntactZip(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path) || new FileInfo(path).Length < 22)
                    return false;

                using (var stream = File.OpenRead(path))
                {
                    if (stream.ReadByte() != 'P' || stream.ReadByte() != 'K')
                        return false;
                }

                using (var archive = ZipFile.OpenRead(path))
                    return archive.Entries.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private static void ExtractZipEntries(string zipPath, string destDir)
        {
            string destRoot = Path.GetFullPath(destDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            EnsureDirectoryPath(destRoot);

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    try
                    {
                        ExtractSingleEntry(entry, destRoot);
                    }
                    catch (Exception ex)
                    {
                        SalsaLogger.Warn("Skipped zip entry " + entry.FullName + ": " + ex.Message);
                    }
                }
            }
        }

        private static void ExtractSingleEntry(ZipArchiveEntry entry, string destRoot)
        {
            string relative = (entry.FullName ?? string.Empty)
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar)
                .TrimStart(Path.DirectorySeparatorChar);

            if (string.IsNullOrWhiteSpace(relative))
                return;

            bool zipDirectory = relative.EndsWith(Path.DirectorySeparatorChar.ToString())
                || string.IsNullOrEmpty(entry.Name)
                || (entry.ExternalAttributes & 0x10) != 0;

            if (zipDirectory)
                relative = relative.TrimEnd(Path.DirectorySeparatorChar);

            string dest = Path.GetFullPath(Path.Combine(destRoot, relative));
            if (!dest.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                return;

            if (zipDirectory)
            {
                EnsureDirectoryPath(dest);
                return;
            }

            EnsureDirectoryPath(Path.GetDirectoryName(dest));

            if (Directory.Exists(dest))
                return;

            ExtractEntryToFile(entry, dest);
        }

        private static void ExtractEntryToFile(ZipArchiveEntry entry, string dest)
        {
            if (File.Exists(dest))
                File.Delete(dest);

            using (Stream source = entry.Open())
            using (var target = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None))
                source.CopyTo(target);
        }

        private static void EnsureDirectoryPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            var missing = new Stack<string>();
            string current = Path.GetFullPath(path);

            while (!string.IsNullOrEmpty(current) && !Directory.Exists(current))
            {
                if (File.Exists(current))
                    File.Delete(current);

                missing.Push(current);

                string parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                    break;

                current = parent;
            }

            while (missing.Count > 0)
                Directory.CreateDirectory(missing.Pop());
        }

        private static void CleanupZips(IEnumerable<RuntimePack> packs)
        {
            foreach (RuntimePack pack in packs)
            {
                try { if (!string.IsNullOrEmpty(pack.ZipPath) && File.Exists(pack.ZipPath)) File.Delete(pack.ZipPath); } catch { }
            }
        }

        private static bool IsDotnetVersionInstalled(string version, string installDir)
        {
            return HasSharedFramework(installDir, "Microsoft.NETCore.App", version)
                && HasSharedFramework(installDir, "Microsoft.WindowsDesktop.App", version)
                && HasSharedFramework(installDir, "Microsoft.AspNetCore.App", version);
        }

        private static bool HasSharedFramework(string installDir, string framework, string version)
        {
            string path = Path.Combine(installDir, "shared", framework);
            if (!Directory.Exists(path))
                return false;

            foreach (string directory in Directory.GetDirectories(path))
            {
                if (!Path.GetFileName(directory).StartsWith(version, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Directory.EnumerateFiles(directory, "*.dll").Any())
                    return true;
            }

            return false;
        }

        private sealed class RuntimePack
        {
            public RuntimePack(string channel, string version, string packName, string akaName, string cdnFolder, string cdnFile)
            {
                Label = $".NET {channel} {packName}";
                Urls = new List<string>();

                if (channel == "5.0")
                {
                    Urls.Add($"https://dotnetcli.azureedge.net/dotnet/{cdnFolder}/{version}/{string.Format(cdnFile, version)}");
                    Urls.Add($"https://builds.dotnet.microsoft.com/dotnet/{cdnFolder}/{version}/{string.Format(cdnFile, version)}");
                }
                else
                {
                    Urls.Add($"https://aka.ms/dotnet/{channel}/{akaName}");
                    Urls.Add($"https://builds.dotnet.microsoft.com/dotnet/{cdnFolder}/{version}/{string.Format(cdnFile, version)}");
                    Urls.Add($"https://dotnetcli.azureedge.net/dotnet/{cdnFolder}/{version}/{string.Format(cdnFile, version)}");
                }
            }

            public string Label { get; }
            public List<string> Urls { get; }
            public string ZipPath { get; set; }
        }
    }
}
