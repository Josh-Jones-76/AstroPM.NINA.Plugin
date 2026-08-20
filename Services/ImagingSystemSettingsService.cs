using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AstroPM.NINA.Plugin.Services
{
    /// <summary>
    /// Pulls the cloud imaging-system list and applies the selected rig's equipment + scheduling
    /// settings into the local <see cref="AstroPMSettings"/>. Used at run time (target fetch /
    /// schedule build) so the rarely-touched remote NINA always reflects the latest settings from
    /// the desktop app. Callers should skip this in Offline/Vacation mode.
    /// </summary>
    public static class ImagingSystemSettingsService
    {
        /// <summary>Distance beyond which the NINA profile location vs. site location is flagged.</summary>
        private const double LocationMismatchKm = 10.0;

        /// <summary>
        /// Applies the selected imaging system's cloud settings. Returns a user-facing warning
        /// string when the desktop app's settings could NOT be applied (or the NINA profile
        /// location disagrees with the rig's site), null when everything is in sync.
        /// Pass the NINA profile's lat/lon to enable the location-mismatch check.
        /// </summary>
        public static async Task<string> ApplySelectedFromCloudAsync(
            AstroPMSettings settings, AstroPMApiService apiService,
            double? ninaLat = null, double? ninaLon = null, CancellationToken token = default)
        {
            string warning;
            try
            {
                var resp = await apiService.ListImagingSystemsAsync(settings.SyncToken, token);
                if (resp == null || !resp.Success || resp.ImagingSystems == null)
                    return Warn("Couldn't reach the Astro PM cloud — using this NINA install's local scheduling settings.");

                ImagingSystemCacheService.Save(resp.ImagingSystems);

                if (string.IsNullOrEmpty(settings.SelectedImagingSystem))
                    return Warn("No Imaging System selected in Astro PM plugin Options — using local scheduling settings, not the desktop app's.");

                var sys = resp.ImagingSystems.FirstOrDefault(s =>
                    string.Equals(s.Name, settings.SelectedImagingSystem, StringComparison.OrdinalIgnoreCase));
                if (sys == null)
                    return Warn($"Imaging system '{settings.SelectedImagingSystem}' was not found in the cloud — using local scheduling settings. Re-select it in Astro PM plugin Options.");

                // Equipment (drives target filtering)
                settings.LocationFilter = sys.SiteName ?? settings.LocationFilter;
                settings.TelescopeFilter = sys.TelescopeName ?? settings.TelescopeFilter;
                settings.CameraFilter = sys.CameraName ?? settings.CameraFilter;

                // Scheduling settings
                var s = sys.SimSettings;
                if (s != null)
                {
                    if (!string.IsNullOrEmpty(s.Strategy)) settings.Strategy = s.Strategy;
                    if (!string.IsNullOrEmpty(s.Playback)) settings.PlaybackMode = s.Playback;
                    if (!string.IsNullOrEmpty(s.SortChain)) settings.SortChain = s.SortChain;
                    settings.BonusEnabled = s.BonusEnabled;
                    settings.OvershootPercent = Math.Max(0, s.OvershootPercent); // legacy -1 → 0
                    settings.MosaicPanelPreference = s.MosaicPanelPreference;
                    settings.DitherEnabled = s.DitherEnabled;
                    settings.DitherEvery = s.DitherEvery;
                    settings.FilterSwitchEnabled = s.FilterSwitchEnabled;
                    settings.FilterSwitchCount = s.FilterSwitchCount;
                    settings.FilterSwitchTolerance = s.FilterSwitchTolerance;
                    settings.FlatsEnabled = s.FlatsEnabled;
                }

                settings.Save();
                AstroPMSettings.NotifyExternallyChanged(); // refresh any open Simulator UI
                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Applied cloud settings for imaging system '{sys.Name}'.");

                if (s == null)
                    return Warn($"Imaging system '{sys.Name}' has no scheduling settings from the desktop yet — save simulator settings in the Astro PM app and Sync to Cloud.");

                // Location check: the schedule here is computed from the NINA profile's
                // observatory coordinates, so a mismatch with the rig's site means the plan
                // will not match the desktop app (visibility, moon safety, twilight times).
                warning = CheckLocationMismatch(sys, ninaLat, ninaLon);
                if (warning != null) return Warn(warning);

                return null;
            }
            catch (Exception ex)
            {
                global::NINA.Core.Utility.Logger.Warning(
                    $"AstroPM | Imaging-system settings refresh failed (keeping local): {ex.Message}");
                return "Couldn't reach the Astro PM cloud — using this NINA install's local scheduling settings.";
            }
        }

        private static string Warn(string message)
        {
            global::NINA.Core.Utility.Logger.Warning($"AstroPM | {message}");
            return message;
        }

        private static string CheckLocationMismatch(Models.ImagingSystemInfo sys, double? ninaLat, double? ninaLon)
        {
            var s = sys?.SimSettings;
            if (s?.SiteLat == null || s.SiteLon == null) return null;
            if (ninaLat == null || ninaLon == null) return null;
            // An unset NINA profile (0,0) is reported by the schedule build itself; skip here.
            if (Math.Abs(ninaLat.Value) < 0.001 && Math.Abs(ninaLon.Value) < 0.001) return null;

            double km = HaversineKm(s.SiteLat.Value, s.SiteLon.Value, ninaLat.Value, ninaLon.Value);
            if (km <= LocationMismatchKm) return null;

            return $"Location mismatch: this NINA profile's observatory location ({ninaLat.Value:F4}, {ninaLon.Value:F4}) " +
                   $"is {km:F0} km from site '{sys.SiteName}' ({s.SiteLat.Value:F4}, {s.SiteLon.Value:F4}). " +
                   "Schedules built here will NOT match the Astro PM desktop app — update the NINA profile's location to the site coordinates.";
        }

        private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
        {
            const double R = 6371.0;
            double dLat = (lat2 - lat1) * Math.PI / 180.0;
            double dLon = (lon2 - lon1) * Math.PI / 180.0;
            double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                       Math.Cos(lat1 * Math.PI / 180.0) * Math.Cos(lat2 * Math.PI / 180.0) *
                       Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
        }
    }
}
