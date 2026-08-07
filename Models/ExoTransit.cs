using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace AstroPM.NINA.Plugin.Models {

    /// <summary>
    /// Minimal exoplanet transit ephemeris carried in the cloud constraints blob
    /// (constraints.exoplanet_json, written by the desktop app). Property names
    /// match the desktop's System.Text.Json PascalCase output. Only the fields
    /// the engine needs are parsed — results/history fields are ignored.
    /// </summary>
    public class ExoEphemeris {
        [JsonProperty("Planet")]
        public string Planet { get; set; } = "";

        [JsonProperty("PeriodDays")]
        public double PeriodDays { get; set; }

        [JsonProperty("T0Bjd")]
        public double T0Bjd { get; set; }

        [JsonProperty("DurationHours")]
        public double DurationHours { get; set; }

        /// <summary>Site-local night date ("yyyy-MM-dd") the project is locked to;
        /// null = legacy rolling mode (any transit night).</summary>
        [JsonProperty("LockedNightDate")]
        public string LockedNightDate { get; set; }

        /// <summary>Baseline padding each side of the transit for the recommended
        /// capture window — the ExoClock/ETD standard 1 h (matches desktop).</summary>
        public const double BaselineHours = 1.0;

        public static ExoEphemeris FromJson(string json) {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try {
                var e = JsonConvert.DeserializeObject<ExoEphemeris>(json);
                if (e == null || e.PeriodDays <= 0 || e.T0Bjd <= 0) return null;
                return e;
            } catch { return null; }
        }

        /// <summary>
        /// The recommended capture window (ingress−1h … egress+1h, UTC) of the transit
        /// overlapping [nightStartUtc, nightEndUtc], or null when no transit falls in
        /// the night (or the project is locked to a different night). Mirrors the
        /// desktop's BuildTargetProfilesStatic exo-window computation exactly.
        /// </summary>
        public (DateTime StartUtc, DateTime EndUtc)? WindowForNight(
            DateTime nightStartUtc, DateTime nightEndUtc,
            double raHours, double decDeg, TimeZoneInfo tz) {

            double halfSpanHrs = DurationHours / 2 + BaselineHours;
            foreach (var (midUtc, _) in ExoTransitMath.TransitsInWindow(
                T0Bjd, PeriodDays, raHours, decDeg,
                nightStartUtc.AddHours(-halfSpanHrs), nightEndUtc.AddHours(halfSpanHrs))) {

                var recStart = midUtc.AddHours(-halfSpanHrs);
                var recEnd = midUtc.AddHours(halfSpanHrs);
                if (recEnd <= nightStartUtc || recStart >= nightEndUtc) continue;
                if (!string.IsNullOrEmpty(LockedNightDate) && tz != null) {
                    // Noon-to-noon night date of the transit (matches desktop TransitOnNight)
                    var localMid = TimeZoneInfo.ConvertTimeFromUtc(midUtc, tz);
                    if (localMid.AddHours(-12).ToString("yyyy-MM-dd") != LockedNightDate)
                        continue;
                }
                return (recStart, recEnd);
            }
            return null;
        }
    }

    /// <summary>
    /// Ephemeris math for transit prediction: BJD_TDB ↔ UTC conversion (including
    /// the barycentric Rømer delay) and transit enumeration from a (T0, period)
    /// ephemeris. Verbatim port of the desktop's Services/TransitMath.cs — the two
    /// must stay identical so desktop plan and plugin execution agree.
    /// </summary>
    public static class ExoTransitMath {
        private const double Deg2Rad = Math.PI / 180.0;
        private const double J2000 = 2451545.0;

        /// <summary>TT − UTC ≈ 32.184 s + 37 leap seconds (constant until the next leap second).</summary>
        private const double TtMinusUtcSec = 69.184;

        /// <summary>Light travel time for 1 AU, in seconds.</summary>
        private const double AuLightSec = 499.00478;

        public static double ToJulianDate(DateTime utc)
            => J2000 + (utc - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;

        public static DateTime FromJulianDate(double jd)
            => new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(jd - J2000);

        /// <summary>
        /// Rømer delay in seconds: projection of Earth's barycentric position onto the
        /// target direction. BJD = JD_geocentric + delay, so subtract it to get Earth
        /// arrival time. Low-precision Meeus solar position (good to a few seconds here).
        /// </summary>
        public static double RomerDelaySec(double jd, double raHours, double decDeg) {
            double n = jd - J2000;
            double g = (357.528 + 0.9856003 * n) * Deg2Rad;              // solar mean anomaly
            double l = (280.460 + 0.9856474 * n) * Deg2Rad;              // solar mean longitude
            double lambda = l + (1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g)) * Deg2Rad;
            double r = 1.00014 - 0.01671 * Math.Cos(g) - 0.00014 * Math.Cos(2 * g); // AU
            double eps = (23.439 - 0.0000004 * n) * Deg2Rad;             // obliquity

            // Geocentric sun (equatorial); Earth's heliocentric position is the opposite vector.
            double sx = r * Math.Cos(lambda);
            double sy = r * Math.Sin(lambda) * Math.Cos(eps);
            double sz = r * Math.Sin(lambda) * Math.Sin(eps);

            double ra = raHours * 15.0 * Deg2Rad;
            double dec = decDeg * Deg2Rad;
            double tx = Math.Cos(dec) * Math.Cos(ra);
            double ty = Math.Cos(dec) * Math.Sin(ra);
            double tz = Math.Sin(dec);

            double proj = -sx * tx - sy * ty - sz * tz; // Earth position · target direction, AU
            return proj * AuLightSec;
        }

        /// <summary>Converts a barycentric BJD_TDB event time to geocentric UTC.</summary>
        public static DateTime BjdTdbToUtc(double bjdTdb, double raHours, double decDeg) {
            double romer = RomerDelaySec(bjdTdb, raHours, decDeg);
            double jdUtc = bjdTdb - (romer + TtMinusUtcSec) / 86400.0;
            return FromJulianDate(jdUtc);
        }

        /// <summary>Converts a geocentric UTC instant to barycentric BJD_TDB.</summary>
        public static double UtcToBjdTdb(DateTime utc, double raHours, double decDeg) {
            double jd = ToJulianDate(utc);
            double romer = RomerDelaySec(jd, raHours, decDeg);
            return jd + (romer + TtMinusUtcSec) / 86400.0;
        }

        /// <summary>
        /// All mid-transit times (UTC) that fall inside [windowStartUtc, windowEndUtc]
        /// for the ephemeris T0 + n·P, plus the epoch number n.
        /// </summary>
        public static IEnumerable<(DateTime midUtc, long epoch)> TransitsInWindow(
            double t0BjdTdb, double periodDays, double raHours, double decDeg,
            DateTime windowStartUtc, DateTime windowEndUtc) {

            if (periodDays <= 0) yield break;

            double bjdStart = UtcToBjdTdb(windowStartUtc, raHours, decDeg);
            double bjdEnd = UtcToBjdTdb(windowEndUtc, raHours, decDeg);

            long nStart = (long)Math.Ceiling((bjdStart - t0BjdTdb) / periodDays);
            long nEnd = (long)Math.Floor((bjdEnd - t0BjdTdb) / periodDays);

            for (long epoch = nStart; epoch <= nEnd; epoch++) {
                double midBjd = t0BjdTdb + epoch * periodDays;
                var midUtc = BjdTdbToUtc(midBjd, raHours, decDeg);
                if (midUtc >= windowStartUtc && midUtc <= windowEndUtc)
                    yield return (midUtc, epoch);
            }
        }
    }
}
