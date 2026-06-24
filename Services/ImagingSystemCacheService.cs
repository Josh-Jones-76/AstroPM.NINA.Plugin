using System;
using System.Collections.Generic;
using System.IO;
using AstroPM.NINA.Plugin.Models;
using Newtonsoft.Json;

namespace AstroPM.NINA.Plugin.Services
{
    /// <summary>
    /// Caches the cloud imaging-system list to disk so the Options page can populate the
    /// Imaging System selector while offline (Vacation Mode or no connectivity).
    /// </summary>
    public static class ImagingSystemCacheService
    {
        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA", "Plugins", "AstroPM.NINA.Plugin");

        private static readonly string CacheFile = Path.Combine(CacheDir, "imaging_systems_cache.json");

        public static void Save(List<ImagingSystemInfo> systems)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                var wrapper = new CacheWrapper { FetchedUtc = DateTime.UtcNow, Systems = systems };
                File.WriteAllText(CacheFile, JsonConvert.SerializeObject(wrapper, Formatting.Indented));
            }
            catch (Exception ex)
            {
                global::NINA.Core.Utility.Logger.Warning($"AstroPM | Failed to write imaging-system cache: {ex.Message}");
            }
        }

        public static CacheWrapper Load()
        {
            try
            {
                if (!File.Exists(CacheFile)) return null;
                var wrapper = JsonConvert.DeserializeObject<CacheWrapper>(File.ReadAllText(CacheFile));
                if (wrapper?.Systems == null) return null;
                return wrapper;
            }
            catch (Exception ex)
            {
                global::NINA.Core.Utility.Logger.Warning($"AstroPM | Failed to read imaging-system cache: {ex.Message}");
                return null;
            }
        }

        public class CacheWrapper
        {
            public DateTime FetchedUtc { get; set; }
            public List<ImagingSystemInfo> Systems { get; set; }
        }
    }
}
