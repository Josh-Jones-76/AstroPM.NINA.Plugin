using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace AstroPM.NINA.Plugin.Models {

    /// <summary>
    /// A custom horizon profile parsed from a .hrz file (the same file NINA loads in
    /// Options → General → Custom Horizon): plain text lines of "azimuth altitude"
    /// pairs, with # comment lines. Provides altitude lookup at any azimuth via linear
    /// interpolation with 0/360 wraparound.
    ///
    /// IMPORTANT: this parser and interpolation are a line-for-line port of the desktop
    /// app's Services/HorizonProfile.cs — both must stay identical so the desktop
    /// simulator (using the site's stored .hrz) and this plugin (using NINA's horizon
    /// file) produce the exact same schedule.
    /// </summary>
    public class HorizonProfile {

        /// <summary>Points sorted by azimuth, azimuth normalized to [0, 360).</summary>
        public IReadOnlyList<(double AzDeg, double AltDeg)> Points { get; }

        private HorizonProfile(List<(double AzDeg, double AltDeg)> points) {
            Points = points;
        }

        /// <summary>
        /// Parses .hrz text. Skips blank lines and #/; comments; accepts space, tab, or
        /// comma separators. Returns null unless at least 2 valid points are found.
        /// </summary>
        public static HorizonProfile Parse(string text) {
            if (string.IsNullOrWhiteSpace(text)) return null;

            // Last occurrence of a duplicate azimuth wins (e.g. 360 wraps onto 0).
            var byAz = new Dictionary<double, double>();
            foreach (var rawLine in text.Split('\n')) {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

                var parts = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                if (!double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var az)) continue;
                if (!double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var alt)) continue;

                az %= 360.0;
                if (az < 0) az += 360.0;
                byAz[az] = Math.Clamp(alt, -90.0, 90.0);
            }

            if (byAz.Count < 2) return null;

            var points = byAz.Select(kv => (kv.Key, kv.Value)).OrderBy(p => p.Key).ToList();
            return new HorizonProfile(points);
        }

        /// <summary>Horizon altitude at the given azimuth (linear interpolation, wraps at 0/360).</summary>
        public double AltitudeAt(double azDeg) {
            var az = azDeg % 360.0;
            if (az < 0) az += 360.0;

            var pts = Points;
            int n = pts.Count;

            // First point with azimuth strictly greater than az
            int i = 0;
            while (i < n && pts[i].AzDeg <= az) i++;

            var p1 = i == 0 ? pts[n - 1] : pts[i - 1];
            var p2 = i == n ? pts[0] : pts[i];

            var span = p2.AzDeg - p1.AzDeg;
            if (span <= 0) span += 360.0;
            var d = az - p1.AzDeg;
            if (d < 0) d += 360.0;

            return span == 0 ? p1.AltDeg : p1.AltDeg + (p2.AltDeg - p1.AltDeg) * (d / span);
        }

        /// <summary>
        /// Loads the custom horizon from the active NINA profile's horizon file
        /// (Options → General → Custom Horizon). Returns null when no horizon file is
        /// configured or it can't be read/parsed. Reads the file fresh on every call so
        /// a re-loaded horizon takes effect on the next schedule build.
        /// </summary>
        public static HorizonProfile LoadFromNinaProfile() {
            try {
                var path = AstroPMPlugin.ProfileService?.ActiveProfile?.AstrometrySettings?.HorizonFilePath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
                return Parse(File.ReadAllText(path));
            } catch {
                return null;
            }
        }
    }
}
