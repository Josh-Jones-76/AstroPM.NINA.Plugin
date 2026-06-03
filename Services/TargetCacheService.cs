using System;
using System.Collections.Generic;
using System.IO;
using AstroPM.NINA.Plugin.Models;
using Newtonsoft.Json;

namespace AstroPM.NINA.Plugin.Services
{
    /// <summary>
    /// Caches the cloud target list to a local JSON file so the instruction set
    /// can fall back to it when the network is unavailable.
    /// </summary>
    public static class TargetCacheService
    {
        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA", "Plugins", "AstroPM.NINA.Plugin");

        private static readonly string CacheFile = Path.Combine(CacheDir, "target_cache.json");

        /// <summary>
        /// Saves the target list to disk with a UTC timestamp.
        /// </summary>
        public static void Save(List<ProjectTarget> targets)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                var wrapper = new CacheWrapper
                {
                    FetchedUtc = DateTime.UtcNow,
                    Targets = targets
                };
                var json = JsonConvert.SerializeObject(wrapper, Formatting.Indented);
                File.WriteAllText(CacheFile, json);
            }
            catch (Exception ex)
            {
                global::NINA.Core.Utility.Logger.Warning($"AstroPM | Failed to write target cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads the cached target list from disk, or null if no cache exists.
        /// </summary>
        public static CacheWrapper Load()
        {
            try
            {
                if (!File.Exists(CacheFile)) return null;
                var json = File.ReadAllText(CacheFile);
                var wrapper = JsonConvert.DeserializeObject<CacheWrapper>(json);
                if (wrapper?.Targets == null || wrapper.Targets.Count == 0) return null;
                return wrapper;
            }
            catch (Exception ex)
            {
                global::NINA.Core.Utility.Logger.Warning($"AstroPM | Failed to read target cache: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns a friendly string describing how old the cache is.
        /// </summary>
        public static string AgeDescription(DateTime fetchedUtc)
        {
            var age = DateTime.UtcNow - fetchedUtc;
            if (age.TotalMinutes < 60) return $"{age.TotalMinutes:F0} minutes ago";
            if (age.TotalHours < 24) return $"{age.TotalHours:F1} hours ago";
            return $"{age.TotalDays:F1} days ago";
        }

        public class CacheWrapper
        {
            public DateTime FetchedUtc { get; set; }
            public List<ProjectTarget> Targets { get; set; }
        }
    }
}
