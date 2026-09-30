using AstroPM.NINA.Plugin.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;

namespace AstroPM.NINA.Plugin.Services {

    /// <summary>One flat set this plugin has taken: identity matches FlatSpec / the desktop's
    /// cloud ledger (filter + MECHANICAL rotator angle + gain/offset/bin), plus when.</summary>
    public class FlatsLedgerEntry {
        public string ProjectName { get; set; } = "";
        /// <summary>NINA filter-wheel name (what the FITS FILTER header carries).</summary>
        public string FilterName { get; set; } = "";
        public double? MechanicalRotation { get; set; }
        public int Gain { get; set; }
        public int Offset { get; set; }
        public int BinX { get; set; } = 1;
        public int BinY { get; set; } = 1;
        public DateTime LastFlatsUtc { get; set; }
    }

    /// <summary>The plugin's own record of flats it has taken, per project — the half of the
    /// picture the desktop can't see until the files sync down and it re-pushes the target.
    /// Auto Flats Per Project reads this together with the cloud ledger the desktop pushes in
    /// each target's constraints (<see cref="FlatsLedgerData"/>).</summary>
    public class FlatsLedgerStore {
        public List<FlatsLedgerEntry> Entries { get; set; } = new List<FlatsLedgerEntry>();

        private static string FilePath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA", "Plugins", "AstroPM.NINA.Plugin", "flats_ledger.json");

        public static FlatsLedgerStore Load() {
            try {
                if (System.IO.File.Exists(FilePath))
                    return JsonConvert.DeserializeObject<FlatsLedgerStore>(System.IO.File.ReadAllText(FilePath)) ?? new FlatsLedgerStore();
            } catch { }
            return new FlatsLedgerStore();
        }

        public void Save() {
            try {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
                System.IO.File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
            } catch { }
        }

        /// <summary>Upsert: same project + filter + camera spec + rotation (within tolerance)
        /// refreshes the timestamp instead of adding a twin.</summary>
        public void Record(string projectName, string filterName, double? mech, int gain, int offset, int binX, int binY, DateTime whenUtc) {
            if (string.IsNullOrWhiteSpace(projectName) || string.IsNullOrWhiteSpace(filterName)) return;
            var hit = Entries.FirstOrDefault(e =>
                string.Equals(e.ProjectName, projectName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.FilterName, filterName, StringComparison.OrdinalIgnoreCase)
                && e.Gain == gain && e.Offset == offset && e.BinX == binX && e.BinY == binY
                && FlatsAutoPolicy.RotationMatches(e.MechanicalRotation, mech));
            if (hit != null) {
                if (whenUtc > hit.LastFlatsUtc) hit.LastFlatsUtc = whenUtc;
                if (mech.HasValue) hit.MechanicalRotation = mech;
                return;
            }
            Entries.Add(new FlatsLedgerEntry {
                ProjectName = projectName, FilterName = filterName, MechanicalRotation = mech,
                Gain = gain, Offset = offset, BinX = binX, BinY = binY, LastFlatsUtc = whenUtc,
            });
        }
    }

    /// <summary>Auto Flats Per Project: decides whether a captured combo still needs flats,
    /// given everything known to exist — the desktop's cloud ledger for the project plus the
    /// plugin's own ledger. Pure functions; the instruction set owns the state.</summary>
    public static class FlatsAutoPolicy {
        /// <summary>Same tolerance the desktop uses when matching a flat's ROTATANG to the
        /// lights' — a dawn set at 100.2° covers lights at 100.7° (encoder jitter), 180° does not.</summary>
        public const double RotationToleranceDeg = 2.0;

        public static bool RotationMatches(double? a, double? b) {
            if (!a.HasValue || !b.HasValue) return true;   // no rotator on one side → can't disagree
            double d = Math.Abs(a.Value - b.Value) % 360.0;
            if (d > 180.0) d = 360.0 - d;
            return d <= RotationToleranceDeg;
        }

        private static bool FilterMatches(string sessionFilter, string specFilter, string ninaFilter) {
            if (string.IsNullOrWhiteSpace(sessionFilter)) return false;
            var f = sessionFilter.Trim();
            return (!string.IsNullOrWhiteSpace(specFilter) && string.Equals(f, specFilter.Trim(), StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(ninaFilter) && string.Equals(f, ninaFilter.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Newest matching flat set for this combo across the cloud ledger and the local
        /// ledger. Returns false when nothing matches; <paramref name="newestUtc"/> is null when a
        /// match exists but carries no timestamp.</summary>
        public static bool FindCoverage(
            string projectName, string specFilter, string ninaFilter, double? mech, int gain, int offset, int binX,
            FlatsLedgerData cloud, FlatsLedgerStore local, out DateTime? newestUtc, out string source) {
            newestUtc = null; source = "";
            bool any = false;

            if (cloud?.Sessions != null) {
                foreach (var s in cloud.Sessions) {
                    if (!FilterMatches(s.Filter, specFilter, ninaFilter)) continue;
                    if (!RotationMatches(s.RotationDeg, mech)) continue;
                    if (s.Gain.HasValue && s.Gain.Value != gain) continue;
                    if (s.Offset.HasValue && s.Offset.Value != offset) continue;
                    if (s.Binning > 0 && s.Binning != binX) continue;
                    any = true;
                    if (DateTime.TryParse(s.LastUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var when)
                        && (!newestUtc.HasValue || when > newestUtc.Value)) { newestUtc = when; source = "desktop"; }
                }
            }

            if (local?.Entries != null && !string.IsNullOrWhiteSpace(projectName)) {
                foreach (var e in local.Entries) {
                    if (!string.Equals(e.ProjectName, projectName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!FilterMatches(e.FilterName, specFilter, ninaFilter)) continue;
                    if (!RotationMatches(e.MechanicalRotation, mech)) continue;
                    if (e.Gain != gain || e.Offset != offset || e.BinX != binX) continue;
                    any = true;
                    if (!newestUtc.HasValue || e.LastFlatsUtc > newestUtc.Value) { newestUtc = e.LastFlatsUtc; source = "plugin"; }
                }
            }
            return any;
        }

        /// <summary>Once Per Project: covered by any matching set. Time Based: covered only by a
        /// matching set newer than the interval.</summary>
        public static bool IsCovered(string mode, int intervalDays, bool anyMatch, DateTime? newestUtc, DateTime nowUtc, out string why) {
            if (!anyMatch) { why = "no flats on record"; return false; }
            if (!string.Equals(mode, "TimeBased", StringComparison.OrdinalIgnoreCase)) {
                why = newestUtc.HasValue ? $"flats on record from {newestUtc.Value:MMM d}" : "flats on record";
                return true;
            }
            int days = Math.Max(1, intervalDays);
            if (!newestUtc.HasValue) { why = $"flats on record but undated — re-taking (every {days} d)"; return false; }
            var age = nowUtc - newestUtc.Value;
            if (age.TotalDays < days) { why = $"flats from {newestUtc.Value:MMM d} ({age.TotalDays:F1} d ago) within {days} d"; return true; }
            why = $"flats from {newestUtc.Value:MMM d} ({age.TotalDays:F1} d ago) older than {days} d";
            return false;
        }
    }
}
