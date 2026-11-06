using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AstroPM.NINA.Plugin.Models;
using Newtonsoft.Json;

namespace AstroPM.NINA.Plugin.Services
{
    /// <summary>
    /// Caches the cloud target list to a local JSON file so the instruction set can fall
    /// back to it when the network is unavailable, and keeps a per-exposure-set capture
    /// ledger on top of it.
    ///
    /// The ledger is what keeps multi-night runs honest when nobody is running the desktop
    /// app: the cloud's accepted/acquired counts only move when the desktop scans the
    /// files, so on its own a nightly fetch would re-image last night's subs. Every light
    /// the plugin captures adds one to the ledger for its exposure set; the counts the
    /// scheduler sees are the cloud numbers plus those local adds. The file always holds
    /// the RAW cloud numbers — the adds are applied in memory by <see cref="Load"/> and
    /// <see cref="SaveFromCloud"/>, never written into the targets themselves.
    ///
    /// Pruning uses the signal the cloud already carries: the desktop pushes counts only
    /// after a file scan changed them, and the acquired count moves on any scan (even one
    /// where every sub was rejected). So when a fetch shows a set's acquired count differs
    /// from the value seen when the adds began, the desktop has counted the files and the
    /// adds are dropped. If the count is unchanged the adds carry forward. A set absent
    /// from a fetch keeps its entry (different writers save different subsets: the Options
    /// browser saves every status, the nightly fetch only Active, Refresh only this rig),
    /// with a 90-day age limit for hygiene.
    ///
    /// Desktops from 1.7.5 also send a per-set "counted_through_utc" (every frame saved before
    /// it was synced and scanned). When present it replaces the acquired-moved test: each add
    /// carries its capture time and exactly the adds before the stamp are dropped. That closes
    /// the gap where a sync + Delete Rejected lands the count back on the baseline.
    /// </summary>
    public static class TargetCacheService
    {
        private static readonly string CacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA", "Plugins", "AstroPM.NINA.Plugin");

        private static readonly string CacheFile = Path.Combine(CacheDir, "target_cache.json");

        private static readonly object FileLock = new object();

        /// <summary>Oldest cache the sequencer will run from when the cloud is merely unreachable
        /// (not in Offline/Vacation Mode).</summary>
        public static readonly TimeSpan MaxOnlineFallbackAge = TimeSpan.FromDays(7);

        /// <summary>Ledger entries with no capture for this long are dropped on the next fetch,
        /// whether or not their exposure set is still around.</summary>
        private static readonly TimeSpan LedgerMaxAge = TimeSpan.FromDays(90);

        /// <summary>Identity of one exposure set across fetches. The cloud has no stable id per
        /// set, so this is the same tuple the desktop uses to match its own rows.</summary>
        public static string SetKey(ProjectTarget target, PanelData panel, ExposureSetData es)
        {
            return string.Join("|",
                target?.TargetName ?? "", target?.LocationName ?? "", panel?.Label ?? "",
                es?.FilterName ?? "", (es?.ExposureLengthSec ?? 0).ToString("F1", CultureInfo.InvariantCulture),
                es?.Gain ?? 0, es?.Offset ?? 0, es?.BinningX ?? 1, es?.BinningY ?? 1)
                .ToLowerInvariant();
        }

        /// <summary>Stores a freshly fetched cloud list (raw numbers), prunes the ledger against
        /// it, then applies the surviving adds to the passed list so the caller schedules on
        /// effective counts.</summary>
        public static void SaveFromCloud(List<ProjectTarget> targets)
        {
            if (targets == null) return;
            lock (FileLock)
            {
                var previous = ReadRaw();
                var ledger = PruneLedger(previous?.Ledger, targets);
                WriteRaw(new CacheWrapper
                {
                    FetchedUtc = DateTime.UtcNow,
                    Targets = targets,
                    Ledger = ledger
                });
                Apply(targets, ledger);
            }
        }

        /// <summary>Loads the cached target list with the capture ledger applied, or null if no
        /// cache exists.</summary>
        public static CacheWrapper Load()
        {
            lock (FileLock)
            {
                var wrapper = ReadRaw();
                if (wrapper?.Targets == null || wrapper.Targets.Count == 0) return null;
                Apply(wrapper.Targets, wrapper.Ledger);
                return wrapper;
            }
        }

        /// <summary>Adds one captured light to the ledger for this exposure set. The in-memory
        /// target objects are NOT touched here — the caller keeps its own running copy.</summary>
        public static void RecordCapture(ProjectTarget target, PanelData panel, ExposureSetData es)
        {
            var key = SetKey(target, panel, es);
            lock (FileLock)
            {
                var wrapper = ReadRaw();
                if (wrapper == null)
                {
                    global::NINA.Core.Utility.Logger.Warning($"AstroPM | Capture ledger: no target cache on disk, capture of {key} not recorded");
                    return;
                }
                wrapper.Ledger = wrapper.Ledger ?? new List<LedgerEntry>();
                var entry = wrapper.Ledger.FirstOrDefault(e => e.Key == key);
                if (entry == null)
                {
                    // Baseline = the cloud's acquired count as stored in the file (raw, never
                    // includes adds). -1 when the set isn't in the file, e.g. it was fetched
                    // through a writer that filtered it out — the next fetch stamps it.
                    var raw = FindSet(wrapper.Targets, key);
                    entry = new LedgerEntry { Key = key, BaselineAcquired = raw?.AcquiredCount ?? -1 };
                    wrapper.Ledger.Add(entry);
                }
                var nowUtc = DateTime.UtcNow;
                entry.CaptureTimesUtc = EffectiveCaptureTimes(entry);
                entry.CaptureTimesUtc.Add(nowUtc);
                entry.LocalAdds = entry.CaptureTimesUtc.Count;
                entry.LastCaptureUtc = nowUtc;
                WriteRaw(wrapper);
                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Capture ledger: {target?.TargetName}/{panel?.Label} {es?.FilterName} {es?.ExposureLengthSec:F0}s → +{entry.LocalAdds} since the desktop last counted (cloud acquired {entry.BaselineAcquired})");
            }
        }

        /// <summary>Total uncounted captures currently in the ledger (for status text).</summary>
        public static int PendingLedgerAdds()
        {
            lock (FileLock)
            {
                var wrapper = ReadRaw();
                return wrapper?.Ledger?.Sum(e => e.LocalAdds) ?? 0;
            }
        }

        /// <summary>Ledger adds that land on the passed targets, one row per exposure set (sets
        /// with no pending adds are left out). Matches by <see cref="SetKey"/>, so it works on
        /// either the raw or the ledger-applied list. Read-only — the reconcile rules are
        /// untouched; this only lets the UI say why its counts differ from the desktop's.</summary>
        public static List<PendingAdd> PendingLedgerAddsFor(IEnumerable<ProjectTarget> targets)
        {
            var result = new List<PendingAdd>();
            if (targets == null) return result;
            Dictionary<string, int> adds;
            lock (FileLock)
            {
                var wrapper = ReadRaw();
                if (wrapper?.Ledger == null || wrapper.Ledger.Count == 0) return result;
                adds = wrapper.Ledger.Where(e => e.LocalAdds > 0 && e.Key != null)
                    .GroupBy(e => e.Key).ToDictionary(g => g.Key, g => g.Sum(e => e.LocalAdds));
            }
            if (adds.Count == 0) return result;
            foreach (var t in targets)
            {
                if (t?.Panels == null) continue;
                foreach (var p in t.Panels)
                {
                    if (p?.ExposureSets == null) continue;
                    foreach (var es in p.ExposureSets)
                    {
                        var key = SetKey(t, p, es);
                        if (!adds.TryGetValue(key, out var n)) continue;
                        adds.Remove(key);   // Apply credits only the first set with a key
                        result.Add(new PendingAdd { Target = t, Panel = p, Set = es, Adds = n });
                    }
                }
            }
            return result;
        }

        /// <summary>Drops the ledger entries for the given set keys (user's "Clear" on the
        /// simulator banner). Needed because the acquired-moved signal can't see a desktop
        /// scan whose net count lands back on the baseline — e.g. sync + Delete Rejected that
        /// leaves exactly as many subs as before the night — so those adds would otherwise
        /// carry forward forever. Returns the number of adds removed.</summary>
        public static int ClearLedgerAdds(IEnumerable<string> keys)
        {
            if (keys == null) return 0;
            var set = new HashSet<string>(keys.Where(k => k != null));
            if (set.Count == 0) return 0;
            lock (FileLock)
            {
                var wrapper = ReadRaw();
                if (wrapper?.Ledger == null || wrapper.Ledger.Count == 0) return 0;
                var removed = wrapper.Ledger.Where(e => e.Key != null && set.Contains(e.Key)).ToList();
                if (removed.Count == 0) return 0;
                wrapper.Ledger = wrapper.Ledger.Except(removed).ToList();
                WriteRaw(wrapper);
                int adds = removed.Sum(e => e.LocalAdds);
                foreach (var e in removed)
                    global::NINA.Core.Utility.Logger.Info($"AstroPM | Capture ledger: {e.Key} — {e.LocalAdds} local adds cleared by user (cloud acquired {e.BaselineAcquired})");
                return adds;
            }
        }

        public class PendingAdd
        {
            public ProjectTarget Target { get; set; }
            public PanelData Panel { get; set; }
            public ExposureSetData Set { get; set; }
            public int Adds { get; set; }
        }

        // ── internals ──

        private static List<LedgerEntry> PruneLedger(List<LedgerEntry> ledger, List<ProjectTarget> fresh)
        {
            var kept = new List<LedgerEntry>();
            if (ledger == null || ledger.Count == 0) return kept;
            var now = DateTime.UtcNow;
            int dropped = 0, carried = 0;
            foreach (var entry in ledger)
            {
                if (entry.LocalAdds <= 0) continue;
                if (now - entry.LastCaptureUtc > LedgerMaxAge)
                {
                    global::NINA.Core.Utility.Logger.Info($"AstroPM | Capture ledger: {entry.Key} — {entry.LocalAdds} adds older than {LedgerMaxAge.TotalDays:F0} days, dropped");
                    continue;
                }
                var set = FindSet(fresh, entry.Key);
                if (set == null)
                {
                    kept.Add(entry);   // not part of this writer's subset — keep as is
                    continue;
                }
                if (set.CountedThroughUtc.HasValue)
                {
                    // The desktop says its counts include every frame saved before this time.
                    // Drop exactly the adds it covers, whether or not the count moved — a
                    // sync followed by Delete Rejected can land back on the same number.
                    var through = AsUtc(set.CountedThroughUtc.Value);
                    var cutoff = through - ClockSkewMargin;
                    var times = EffectiveCaptureTimes(entry);
                    var keep = times.Where(t => t > cutoff).ToList();
                    int covered = times.Count - keep.Count;
                    if (covered > 0)
                        global::NINA.Core.Utility.Logger.Info(
                            $"AstroPM | Capture ledger: {entry.Key} — desktop counted through {through:yyyy-MM-dd HH:mm:ss}Z, dropping {covered} of {times.Count} local adds (cloud acquired {set.AcquiredCount})");
                    entry.CaptureTimesUtc = keep;
                    entry.LocalAdds = keep.Count;
                    entry.BaselineAcquired = set.AcquiredCount;
                    if (keep.Count == 0) { dropped++; continue; }
                    kept.Add(entry);
                    carried++;
                    continue;
                }
                if (entry.BaselineAcquired < 0)
                {
                    entry.BaselineAcquired = set.AcquiredCount;   // first sighting since the adds began
                    kept.Add(entry);
                    carried++;
                    continue;
                }
                if (set.AcquiredCount != entry.BaselineAcquired)
                {
                    // The desktop scanned this set's files since the adds began: the cloud
                    // numbers now include them (or rejected them). Cloud wins.
                    global::NINA.Core.Utility.Logger.Info(
                        $"AstroPM | Capture ledger: {entry.Key} — cloud acquired moved {entry.BaselineAcquired} → {set.AcquiredCount}, dropping {entry.LocalAdds} local adds (desktop has counted)");
                    dropped++;
                    continue;
                }
                kept.Add(entry);
                carried++;
            }
            if (dropped > 0 || carried > 0)
                global::NINA.Core.Utility.Logger.Info($"AstroPM | Capture ledger after fetch: {carried} set(s) carried forward, {dropped} reconciled with the cloud");
            return kept;
        }

        /// <summary>Allowance for clock difference between the desktop PC and the NINA PC (and for
        /// a frame still being written when the sync listed the folder). An add captured within
        /// this window before the stamp is kept — at worst one sub stays double-counted until
        /// the next sync, never a silently lost one.</summary>
        private static readonly TimeSpan ClockSkewMargin = TimeSpan.FromMinutes(2);

        private static DateTime AsUtc(DateTime t) =>
            t.Kind == DateTimeKind.Utc ? t
            : t.Kind == DateTimeKind.Local ? t.ToUniversalTime()
            : DateTime.SpecifyKind(t, DateTimeKind.Utc);

        /// <summary>One UTC time per add, oldest first. Adds without a recorded time (entries from
        /// before per-capture times existed) are placed at LastCaptureUtc — the latest they could
        /// be, so a stamp only drops them once it is past every capture.</summary>
        private static List<DateTime> EffectiveCaptureTimes(LedgerEntry entry)
        {
            var times = (entry.CaptureTimesUtc ?? new List<DateTime>()).Select(AsUtc).ToList();
            if (times.Count > entry.LocalAdds) times = times.Skip(times.Count - entry.LocalAdds).ToList();
            var last = AsUtc(entry.LastCaptureUtc);
            while (times.Count < entry.LocalAdds) times.Add(last);
            times.Sort();
            return times;
        }

        private static void Apply(List<ProjectTarget> targets, List<LedgerEntry> ledger)
        {
            if (targets == null || ledger == null || ledger.Count == 0) return;
            int sets = 0, subs = 0;
            foreach (var entry in ledger)
            {
                if (entry.LocalAdds <= 0) continue;
                var set = FindSet(targets, entry.Key);
                if (set == null) continue;
                set.AcquiredCount += entry.LocalAdds;
                set.AcceptedCount += entry.LocalAdds;   // optimistic: counts toward Remaining until the desktop says otherwise
                sets++;
                subs += entry.LocalAdds;
            }
            if (subs > 0)
                global::NINA.Core.Utility.Logger.Info($"AstroPM | Capture ledger applied: {subs} sub(s) across {sets} exposure set(s) not yet counted by the desktop");
        }

        private static ExposureSetData FindSet(List<ProjectTarget> targets, string key)
        {
            if (targets == null) return null;
            foreach (var t in targets)
            {
                if (t?.Panels == null) continue;
                foreach (var p in t.Panels)
                {
                    if (p?.ExposureSets == null) continue;
                    foreach (var es in p.ExposureSets)
                        if (SetKey(t, p, es) == key) return es;
                }
            }
            return null;
        }

        private static CacheWrapper ReadRaw()
        {
            try
            {
                if (!File.Exists(CacheFile)) return null;
                var wrapper = JsonConvert.DeserializeObject<CacheWrapper>(File.ReadAllText(CacheFile));
                if (wrapper == null) return null;
                wrapper.Ledger = wrapper.Ledger ?? new List<LedgerEntry>();
                return wrapper;
            }
            catch (Exception ex)
            {
                global::NINA.Core.Utility.Logger.Warning($"AstroPM | Failed to read target cache: {ex.Message}");
                return null;
            }
        }

        private static void WriteRaw(CacheWrapper wrapper)
        {
            try
            {
                Directory.CreateDirectory(CacheDir);
                File.WriteAllText(CacheFile, JsonConvert.SerializeObject(wrapper, Formatting.Indented));
            }
            catch (Exception ex)
            {
                global::NINA.Core.Utility.Logger.Warning($"AstroPM | Failed to write target cache: {ex.Message}");
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
            public List<LedgerEntry> Ledger { get; set; } = new List<LedgerEntry>();
        }

        public class LedgerEntry
        {
            public string Key { get; set; }
            /// <summary>Cloud acquired count when the adds began; -1 = not yet seen in a fetch.</summary>
            public int BaselineAcquired { get; set; } = -1;
            public int LocalAdds { get; set; }
            public DateTime LastCaptureUtc { get; set; }
            /// <summary>UTC time of each add, oldest first, so a desktop "counted through" stamp
            /// can drop exactly the adds it covers. Entries written before 1.7.5 have none —
            /// their untimed adds are treated as captured at <see cref="LastCaptureUtc"/>.</summary>
            public List<DateTime> CaptureTimesUtc { get; set; }
        }
    }
}
