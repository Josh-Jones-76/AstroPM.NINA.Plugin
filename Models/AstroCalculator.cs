using System;

namespace AstroPM.NINA.Plugin.Models {

    public class ObservingConstraints {
        public double SunAltitudeThreshold { get; set; } = -18.0;
        public double MinTargetAltitude { get; set; } = 30.0;
        public bool MoonAvoidanceEnabled { get; set; }
        public double MaxMoonIlluminationPct { get; set; } = 40.0;
        public double MinMoonSeparationDeg { get; set; } = 60.0;
        public double MaxMoonAltitude { get; set; } = 5.0;
        public double MinMoonAltitude { get; set; } = -15.0;
        public double MoonAvoidanceWidthDays { get; set; } = 7.0;
        public double MoonRelaxScale { get; set; } = 0.0;
        public double MinTimeOnTargetHrs { get; set; } = 1.0;
    }

    public static class AstroCalculator {

        private const double Deg2Rad = Math.PI / 180.0;
        private const double Rad2Deg = 180.0 / Math.PI;
        private const double SynodicPeriod = 29.53059;
        private static readonly DateTime NewMoonEpoch = new DateTime(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc);

        public static double SunAltitudeAtTime(DateTime utc, double latDeg, double lonDeg) {
            var jd = ToJulianDate(utc);
            var n = jd - 2451545.0;
            var sunLon = (280.46 + 0.9856474 * n) % 360.0;
            if (sunLon < 0) sunLon += 360.0;

            var obliquity = 23.44 * Deg2Rad;
            var sunLonRad = sunLon * Deg2Rad;
            var sunDecRad = Math.Asin(Math.Sin(obliquity) * Math.Sin(sunLonRad));

            var sunRaRad = Math.Atan2(Math.Sin(sunLonRad) * Math.Cos(obliquity), Math.Cos(sunLonRad));
            var sunRaHours = ((sunRaRad * Rad2Deg / 15.0) + 24.0) % 24.0;

            var lst = LocalSiderealTime(utc, lonDeg);
            var ha = (lst - sunRaHours) * 15.0;
            var latRad = latDeg * Deg2Rad;
            var haRad = ha * Deg2Rad;

            var sinAlt = Math.Sin(latRad) * Math.Sin(sunDecRad) +
                         Math.Cos(latRad) * Math.Cos(sunDecRad) * Math.Cos(haRad);

            return Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0)) * Rad2Deg;
        }

        public static double MoonAltitudeAtMidnight(DateTime utc, double latDeg, double lonDeg) {
            var moonRaDec = MoonRaDec(utc, lonDeg);

            var lst = LocalSiderealTime(utc, lonDeg);
            var ha = (lst - moonRaDec.raHours) * 15.0;
            var latRad = latDeg * Deg2Rad;
            var haRad = ha * Deg2Rad;
            var decRad = moonRaDec.decDeg * Deg2Rad;

            var sinAlt = Math.Sin(latRad) * Math.Sin(decRad) +
                         Math.Cos(latRad) * Math.Cos(decRad) * Math.Cos(haRad);

            return Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0)) * Rad2Deg;
        }

        public static double MoonIllumination(DateTime utcDate) {
            var daysSinceEpoch = (utcDate - NewMoonEpoch).TotalDays;
            var phase = daysSinceEpoch % SynodicPeriod;
            if (phase < 0) phase += SynodicPeriod;
            return (1.0 - Math.Cos(2.0 * Math.PI * phase / SynodicPeriod)) / 2.0 * 100.0;
        }

        public static double TargetAltitudeAtTime(DateTime utc, double raHours, double decDeg,
            double latDeg, double lonDeg) {
            var lst = LocalSiderealTime(utc, lonDeg);
            var ha = (lst - raHours) * 15.0;

            var latRad = latDeg * Deg2Rad;
            var decRad = decDeg * Deg2Rad;
            var haRad = ha * Deg2Rad;

            var sinAlt = Math.Sin(latRad) * Math.Sin(decRad) +
                         Math.Cos(latRad) * Math.Cos(decRad) * Math.Cos(haRad);

            var alt = Math.Asin(Math.Clamp(sinAlt, -1.0, 1.0)) * Rad2Deg;
            return Math.Max(0, alt);
        }

        /// <summary>
        /// Target azimuth at a specific UTC time, degrees from North (0=N, 90=E, 180=S, 270=W).
        /// Same hour-angle math as TargetAltitudeAtTime so alt/az pairs are consistent.
        /// Line-for-line port of the desktop AstroCalculator — keep identical.
        /// </summary>
        public static double TargetAzimuthAtTime(DateTime utc, double raHours, double decDeg,
            double latDeg, double lonDeg) {
            var lst = LocalSiderealTime(utc, lonDeg);
            var ha = (lst - raHours) * 15.0;

            var latRad = latDeg * Deg2Rad;
            var decRad = decDeg * Deg2Rad;
            var haRad = ha * Deg2Rad;

            var y = -Math.Sin(haRad) * Math.Cos(decRad);
            var x = Math.Sin(decRad) * Math.Cos(latRad) - Math.Cos(decRad) * Math.Cos(haRad) * Math.Sin(latRad);

            var az = Math.Atan2(y, x) * Rad2Deg;
            if (az < 0) az += 360.0;
            return az;
        }

        public static double MoonTargetSeparation(DateTime utc, double targetRaHours, double targetDecDeg, double lonDeg) {
            var moonRaDec = MoonRaDec(utc, lonDeg);
            var moonRaDeg = moonRaDec.raHours * 15.0;
            var targetRaDeg = targetRaHours * 15.0;

            var ra1 = moonRaDeg * Deg2Rad;
            var dec1 = moonRaDec.decDeg * Deg2Rad;
            var ra2 = targetRaDeg * Deg2Rad;
            var dec2 = targetDecDeg * Deg2Rad;

            var cosD = Math.Sin(dec1) * Math.Sin(dec2) +
                       Math.Cos(dec1) * Math.Cos(dec2) * Math.Cos(ra1 - ra2);

            return Math.Acos(Math.Clamp(cosD, -1.0, 1.0)) * Rad2Deg;
        }

        public static double RequiredMoonSeparation(DateTime sampleUtc, double moonAltDeg, ObservingConstraints c) {
            if (!c.MoonAvoidanceEnabled) return 0.0;
            if (moonAltDeg <= 0) return 0.0;
            if (c.MinMoonSeparationDeg <= 0) return 0.0;

            var daysSinceNew = (sampleUtc - NewMoonEpoch).TotalDays % SynodicPeriod;
            if (daysSinceNew < 0) daysSinceNew += SynodicPeriod;
            var daysFromFull = Math.Abs(daysSinceNew - SynodicPeriod / 2.0);

            var effectiveSep = c.MinMoonSeparationDeg;
            var effectiveWidth = Math.Max(0.1, c.MoonAvoidanceWidthDays);

            if (c.MoonRelaxScale > 0 && moonAltDeg <= c.MaxMoonAltitude) {
                effectiveSep = Math.Max(0, c.MinMoonSeparationDeg + c.MoonRelaxScale * (moonAltDeg - c.MaxMoonAltitude));
                var altRange = c.MaxMoonAltitude - c.MinMoonAltitude;
                if (altRange > 0)
                    effectiveWidth = Math.Max(0.1, c.MoonAvoidanceWidthDays * Math.Max(0, (moonAltDeg - c.MinMoonAltitude) / altRange));
            }

            var requiredSep = effectiveSep / (1.0 + (daysFromFull * daysFromFull) / (effectiveWidth * effectiveWidth));
            return Math.Max(0, requiredSep);
        }

        private static (double raHours, double decDeg) MoonRaDec(DateTime utc, double lonDeg) {
            var jd = ToJulianDate(utc);
            var daysSinceEpoch = (utc - NewMoonEpoch).TotalDays;
            var n = jd - 2451545.0;

            var sunLon = (280.46 + 0.9856474 * n) % 360.0;
            if (sunLon < 0) sunLon += 360.0;

            var moonLon = (sunLon + (daysSinceEpoch % SynodicPeriod) * (360.0 / SynodicPeriod)) % 360.0;
            if (moonLon < 0) moonLon += 360.0;

            var moonLat = 5.14 * Math.Sin(2.0 * Math.PI * daysSinceEpoch / 27.2122);

            var obliquity = 23.44 * Deg2Rad;
            var moonLonRad = moonLon * Deg2Rad;
            var moonLatRad = moonLat * Deg2Rad;

            var moonRaRad = Math.Atan2(
                Math.Sin(moonLonRad) * Math.Cos(obliquity) - Math.Tan(moonLatRad) * Math.Sin(obliquity),
                Math.Cos(moonLonRad));
            var moonDecRad = Math.Asin(
                Math.Sin(moonLatRad) * Math.Cos(obliquity) +
                Math.Cos(moonLatRad) * Math.Sin(obliquity) * Math.Sin(moonLonRad));

            var raHours = (moonRaRad * Rad2Deg / 15.0 + 24.0) % 24.0;
            var decDeg = moonDecRad * Rad2Deg;

            return (raHours, decDeg);
        }

        private static double LocalSiderealTime(DateTime utc, double lonDeg) {
            var jd = ToJulianDate(utc);
            var t = (jd - 2451545.0) / 36525.0;

            var gmst = 280.46061837 + 360.98564736629 * (jd - 2451545.0) +
                       0.000387933 * t * t - t * t * t / 38710000.0;

            var lst = (gmst + lonDeg) % 360.0;
            if (lst < 0) lst += 360.0;

            return lst / 15.0;
        }

        private static double ToJulianDate(DateTime utc) {
            var y = utc.Year;
            var m = utc.Month;
            var d = utc.Day + utc.TimeOfDay.TotalDays;

            if (m <= 2) { y--; m += 12; }

            var a = y / 100;
            var b = 2 - a + a / 4;

            return (int)(365.25 * (y + 4716)) + (int)(30.6001 * (m + 1)) + d + b - 1524.5;
        }
    }
}
