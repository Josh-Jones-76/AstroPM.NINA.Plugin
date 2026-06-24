using System;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Sequencer.SequenceItem;
using AstroPM.NINA.Plugin.Services;

namespace AstroPM.NINA.Plugin.Instructions
{
    [ExportMetadata("Name", "AstroPM Refresh Cloud Targets")]
    [ExportMetadata("Description", "Fetches the latest active targets from the Astro PM cloud and updates the local cache")]
    [ExportMetadata("Icon", "LoopSVG")]
    [ExportMetadata("Category", "Astro PM Tools")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class RefreshCloudTargets : SequenceItem
    {
        private string _statusText = "Not run yet";
        private bool _fetchSuccess;
        private int _targetCount;
        private string _lastFetchTime = "";

        public string StatusText
        {
            get => _statusText;
            set { _statusText = value; RaisePropertyChanged(); }
        }

        public bool FetchSuccess
        {
            get => _fetchSuccess;
            set { _fetchSuccess = value; RaisePropertyChanged(); }
        }

        public int TargetCount
        {
            get => _targetCount;
            set { _targetCount = value; RaisePropertyChanged(); }
        }

        public string LastFetchTime
        {
            get => _lastFetchTime;
            set { _lastFetchTime = value; RaisePropertyChanged(); }
        }

        [ImportingConstructor]
        public RefreshCloudTargets() { }

        private RefreshCloudTargets(RefreshCloudTargets clone) : this()
        {
            CopyMetaData(clone);
        }

        public override object Clone() => new RefreshCloudTargets(this);

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token)
        {
            var settings = AstroPMSettings.Load();
            if (string.IsNullOrEmpty(settings.SyncToken))
            {
                FetchSuccess = false;
                StatusText = "No sync token configured";
                Notification.ShowWarning("AstroPM Refresh: No sync token configured. Open plugin Options to connect.");
                return;
            }

            progress?.Report(new ApplicationStatus { Status = "AstroPM: Refreshing cloud targets..." });

            // Offline/Vacation Mode: don't fetch — report the existing cache and leave it untouched.
            if (settings.OfflineMode)
            {
                var cache = TargetCacheService.Load();
                TargetCount = cache?.Targets?.Count ?? 0;
                FetchSuccess = cache != null;
                LastFetchTime = "Last Updated: " + DateTime.Now.ToString("MMM d, yyyy h:mm tt");
                StatusText = cache != null
                    ? $"Offline/Vacation Mode — using cache from {TargetCacheService.AgeDescription(cache.FetchedUtc)} ({TargetCount} targets)"
                    : "Offline/Vacation Mode — no cached targets available";
                Logger.Info("AstroPM Refresh | Offline/Vacation Mode — skipped cloud fetch.");
                return;
            }

            var apiService = new AstroPMApiService();

            // Refresh this rig's settings from the cloud FIRST so the run uses the current strategy,
            // dither, filter-switch, etc. (and up-to-date site/telescope/camera for filtering). The
            // remote NINA is rarely opened, so settings changed in the desktop app must land here.
            await ImagingSystemSettingsService.ApplySelectedFromCloudAsync(settings, apiService, token);

            try
            {
                var response = await apiService.ListTargetsAsync(settings.SyncToken, "Active", token);
                if (response.Success && response.Targets != null)
                {
                    // Filter by Location/Telescope/Camera from universal settings
                    var targets = response.Targets.AsEnumerable();
                    if (!string.IsNullOrEmpty(settings.LocationFilter))
                        targets = targets.Where(t => string.Equals(t.LocationName, settings.LocationFilter, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(settings.TelescopeFilter))
                        targets = targets.Where(t => string.Equals(t.TelescopeName, settings.TelescopeFilter, StringComparison.OrdinalIgnoreCase));
                    if (!string.IsNullOrEmpty(settings.CameraFilter))
                        targets = targets.Where(t => string.Equals(t.CameraName, settings.CameraFilter, StringComparison.OrdinalIgnoreCase));
                    var filtered = targets.ToList();

                    TargetCacheService.Save(filtered);

                    var activeCount = filtered.Count;
                    var withRemaining = filtered.Count(t =>
                        t.Panels != null && t.Panels.Any(p =>
                            p.ExposureSets != null && p.ExposureSets.Any(es => es.Remaining > 0)));

                    TargetCount = activeCount;
                    FetchSuccess = true;
                    LastFetchTime = "Last Updated: " + DateTime.Now.ToString("MMM d, yyyy h:mm tt");
                    StatusText = $"{activeCount} targets fetched ({withRemaining} with remaining work)";

                    Logger.Info($"AstroPM Refresh | Fetched {activeCount} active targets, {withRemaining} with remaining exposures");
                    Notification.ShowSuccess($"AstroPM: Refreshed {activeCount} active targets from cloud.");
                }
                else
                {
                    FetchSuccess = false;
                    LastFetchTime = "Last Updated: " + DateTime.Now.ToString("MMM d, yyyy h:mm tt");
                    StatusText = response.Message ?? "Cloud returned an error";
                    Logger.Warning($"AstroPM Refresh | Cloud error: {response.Message}");
                    Notification.ShowWarning($"AstroPM Refresh: {response.Message ?? "Failed to fetch targets."}");
                }
            }
            catch (Exception ex)
            {
                FetchSuccess = false;
                LastFetchTime = "Last Updated: " + DateTime.Now.ToString("MMM d, yyyy h:mm tt");

                // Fall back to cache
                var cache = TargetCacheService.Load();
                if (cache != null)
                {
                    TargetCount = cache.Targets.Count;
                    var age = TargetCacheService.AgeDescription(cache.FetchedUtc);
                    StatusText = $"Cloud unavailable — using cache from {age}";
                    Logger.Warning($"AstroPM Refresh | Cloud failed ({ex.Message}), using cached targets from {age}");
                }
                else
                {
                    TargetCount = 0;
                    StatusText = $"Cloud unavailable — no cache available";
                    Logger.Error($"AstroPM Refresh | Cloud failed and no cache: {ex.Message}");
                }
                Notification.ShowWarning($"AstroPM Refresh: Could not reach cloud — {ex.Message}");
            }

            progress?.Report(new ApplicationStatus { Status = "" });
        }

        public override string ToString() => $"Category: Astro PM Tools, Item: {nameof(RefreshCloudTargets)}";
    }
}
