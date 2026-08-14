using System;
using System.IO;
using System.Linq;

namespace SalsaNOW
{
    // Steam has a JS chunk file (chunk~2dcc5aaf7.js) that we swap out.
    // The hash in the filename changes on Steam updates.
    // Instead of hardcoding it, we find the file by looking for the largest
    // .js file in the steamui folder.
    internal static class SteamChunkDetector
    {
        private static readonly string STEAM_UI_DIR = @"C:\Program Files (x86)\Steam\steamui";

        public static string DetectChunk()
        {
            try
            {
                if (!Directory.Exists(STEAM_UI_DIR))
                    return "chunk~2dcc5aaf7.js";

                var chunks = Directory.GetFiles(STEAM_UI_DIR, "chunk~*.js")
                    .OrderByDescending(f => new FileInfo(f).Length)
                    .ToList();

                if (chunks.Count == 0)
                    return "chunk~2dcc5aaf7.js";

                string knownChunk = "chunk~2dcc5aaf7.js";
                string knownPath = Path.Combine(STEAM_UI_DIR, knownChunk);
                if (File.Exists(knownPath))
                    return knownChunk;

                foreach (var chunk in chunks)
                {
                    try
                    {
                        string content = File.ReadAllText(chunk);
                        if (content.Contains("geforce") || content.Contains("GeForce") ||
                            content.Contains("GFN") || content.Contains("cloudgaming"))
                        {
                            return Path.GetFileName(chunk);
                        }
                    }
                    catch { }
                }

                return Path.GetFileName(chunks[0]);
            }
            catch { return "chunk~2dcc5aaf7.js"; }
        }
    }
}
