using System;
using System.Collections.Generic;

namespace SalsaNOW
{
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