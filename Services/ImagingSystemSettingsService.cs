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
        public static async Task ApplySelectedFromCloudAsync(
            AstroPMSettings settings, AstroPMApiService apiService, CancellationToken token = default)
        {
            try
            {
                var resp = await apiService.ListImagingSystemsAsync(settings.SyncToken, token);
                if (resp == null || !resp.Success || resp.ImagingSystems == null) return;

                ImagingSystemCacheService.Save(resp.ImagingSystems);

                if (string.IsNullOrEmpty(settings.SelectedImagingSystem)) return;
                var sys = resp.ImagingSystems.FirstOrDefault(s =>
                    string.Equals(s.Name, settings.SelectedImagingSystem, StringComparison.OrdinalIgnoreCase));
                if (sys == null) return;

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
                    settings.MosaicPanelPreference = s.MosaicPanelPreference;
                    settings.DitherEnabled = s.DitherEnabled;
                    settings.DitherEvery = s.DitherEvery;
                    settings.FilterSwitchEnabled = s.FilterSwitchEnabled;
                    settings.FilterSwitchCount = s.FilterSwitchCount;
                    settings.FilterSwitchTolerance = s.FilterSwitchTolerance;
                }

                settings.Save();
                AstroPMSettings.NotifyExternallyChanged(); // refresh any open Simulator UI
                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Applied cloud settings for imaging system '{sys.Name}'.");
            }
            catch (Exception ex)
            {
                global::NINA.Core.Utility.Logger.Warning(
                    $"AstroPM | Imaging-system settings refresh failed (keeping local): {ex.Message}");
            }
        }
    }
}
