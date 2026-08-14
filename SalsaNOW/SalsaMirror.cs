using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace SalsaNOW
{
    // HOW MIRRORS WORK:
    // 1. This app starts with ONE hardcoded domain (see BOOTSTRAP below).
    // 2. On startup it tries to download /mirrors.txt from that domain.
    //    That file is just a text list of all your mirror URLs, one per line.
    // 3. If mirrors.txt exists, the app uses ALL those URLs and cycles through
    //    them when something fails. The URLs never appear in source code.
    // 4. If mirrors.txt doesnt exist yet, it just uses the single bootstrap domain.
    //
    // To set it up: upload a mirrors.txt to your server with one URL per line.
    // Example contents:
    //   https://salsanowfiles.work
    //   https://your-backup-domain.com
    //   https://another-mirror.net
    //
    // The bootstrap domain below is the only one NVIDIA can see in source.
    // If it gets blocked, change it here and rebuild.
    internal static class SalsaMirror
    {
        // this is the only hardcoded URL — everything else comes from mirrors.txt
        private static readonly string BOOTSTRAP = "https://salsanowfiles.work";
        private static readonly string MIRROR_LIST_PATH = "/mirrors.txt";

        private static List<string> _activeMirrors = null;
        private static DateTime _lastUpdate = DateTime.MinValue;
        private static readonly TimeSpan CACHE_TTL = TimeSpan.FromHours(6);
        private static readonly object _lock = new object();

        public static async Task<List<string>> GetMirrorsAsync()
        {
            lock (_lock)
            {
                if (_activeMirrors != null && DateTime.Now - _lastUpdate < CACHE_TTL)
                    return _activeMirrors;
            }

            var mirrors = new List<string>();

            // 1. local override file (if someone drops a mirrors.txt next to the exe)
            try
            {
                string localFile = Path.Combine(
                    Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
                    "mirrors.txt");
                if (File.Exists(localFile))
                {
                    var custom = File.ReadAllLines(localFile)
                        .Where(l => !string.IsNullOrWhiteSpace(l) && !l.TrimStart().StartsWith("#"))
                        .Select(l => l.Trim())
                        .ToList();
                    if (custom.Count > 0)
                    {
                        lock (_lock) { _activeMirrors = custom; _lastUpdate = DateTime.Now; }
                        return _activeMirrors;
                    }
                }
            }
            catch { }

            // 2. fetch mirror list from bootstrap server
            // this way NVIDIA cant see the real mirrors in source, only the bootstrap
            try
            {
                using (var wc = new WebClient())
                {
                    wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0";
                    string list = await wc.DownloadStringTaskAsync(BOOTSTRAP + MIRROR_LIST_PATH);
                    var fetched = list.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                        .Where(l => !l.TrimStart().StartsWith("#") && !string.IsNullOrWhiteSpace(l))
                        .Select(l => l.Trim())
                        .ToList();
                    if (fetched.Count > 0)
                        mirrors.AddRange(fetched);
                }
            }
            catch { }

            // 3. fall back to bootstrap if server fetch failed
            if (mirrors.Count == 0)
                mirrors.Add(BOOTSTRAP);

            lock (_lock) { _activeMirrors = mirrors; _lastUpdate = DateTime.Now; }
            return _activeMirrors;
        }

        public static async Task<string> DownloadStringAsync(string path)
        {
            var mirrors = await GetMirrorsAsync();
            Exception lastError = null;
            foreach (var mirror in mirrors)
            {
                try
                {
                    string url = mirror.TrimEnd('/') + path;
                    using (var wc = new WebClient())
                        return await wc.DownloadStringTaskAsync(new Uri(url));
                }
                catch (Exception ex) { lastError = ex; }
            }
            throw new Exception("All mirrors failed for: " + path + " (" + lastError?.Message + ")");
        }

        public static async Task DownloadFileAsync(string path, string localPath)
        {
            var mirrors = await GetMirrorsAsync();
            Exception lastError = null;
            foreach (var mirror in mirrors)
            {
                try
                {
                    string url = mirror.TrimEnd('/') + path;
                    using (var wc = new WebClient())
                    {
                        await wc.DownloadFileTaskAsync(new Uri(url), localPath);
                        return;
                    }
                }
                catch (Exception ex) { lastError = ex; }
            }
            throw new Exception("All mirrors failed for: " + path + " (" + lastError?.Message + ")");
        }
    }
}
