using System.Collections.Generic;
using Newtonsoft.Json;

namespace AstroPM.NINA.Plugin.Models
{
    /// <summary>
    /// An imaging system (rig) pushed from the Astro PM desktop app to the cloud.
    /// The plugin picks one to identify which rig this NINA instance is; site/telescope/camera
    /// are then read-only and driven by the selection. (sim_settings is reserved for a later phase.)
    /// </summary>
    public class ImagingSystemInfo
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("enabled")]
        public bool Enabled { get; set; }

        [JsonProperty("site_name")]
        public string SiteName { get; set; }

        [JsonProperty("telescope_name")]
        public string TelescopeName { get; set; }

        [JsonProperty("camera_name")]
        public string CameraName { get; set; }

        // The rig's scheduling settings, authored in the Astro PM desktop simulator.
        [JsonProperty("sim_settings")]
        public PluginSimSettings SimSettings { get; set; }
    }

    /// <summary>Per-imaging-system scheduling settings pushed from the desktop. Property names match the
    /// desktop's SimulatorSettings (PascalCase) so they deserialize 1:1.</summary>
    public class PluginSimSettings
    {
        [JsonProperty("Strategy")] public string Strategy { get; set; } = "SharedTime";
        [JsonProperty("Playback")] public string Playback { get; set; } = "TimeAware";
        [JsonProperty("SortChain")] public string SortChain { get; set; } = "";
        [JsonProperty("BonusEnabled")] public bool BonusEnabled { get; set; } = true;
        // Guaranteed extra subs per exposure set as a percent of its planned count
        // (insurance frames for SubInspector rejections). 0 or legacy -1 = none.
        [JsonProperty("OvershootPercent")] public int OvershootPercent { get; set; } = 0;
        // Min-Time Tolerance: how far below Min Time on Target a block may fall and still be
        // scheduled (fraction). Older desktops don't send it → default 0.5.
        [JsonProperty("MinTimeTolerance")] public double MinTimeTolerance { get; set; } = 0.5;
        [JsonProperty("MosaicPanelPreference")] public bool MosaicPanelPreference { get; set; } = true;
        [JsonProperty("DitherEnabled")] public bool DitherEnabled { get; set; } = true;
        [JsonProperty("DitherEvery")] public int DitherEvery { get; set; } = 3;
        [JsonProperty("FilterSwitchEnabled")] public bool FilterSwitchEnabled { get; set; } = true;
        [JsonProperty("FilterSwitchCount")] public int FilterSwitchCount { get; set; } = 20;
        [JsonProperty("FilterSwitchTolerance")] public double FilterSwitchTolerance { get; set; } = 0.5;
        [JsonProperty("FlatsEnabled")] public bool FlatsEnabled { get; set; } = false;
        [JsonProperty("FlatsFullSet")] public bool FlatsFullSet { get; set; } = false;
        // Auto Flats Per Project: with flats on, take a project's flats only when it needs them
        // ("OncePerProject" = combos it has no flats for yet, the morning after it's imaged;
        // "TimeBased" = every FlatsAutoIntervalDays from its last flat session). A night that
        // ends under a safety hold makes its flats up the following morning. Off = every
        // combo used, every night. Older desktops don't send these → defaults = off.
        [JsonProperty("FlatsAutoPerProject")] public bool FlatsAutoPerProject { get; set; } = false;
        [JsonProperty("FlatsAutoMode")] public string FlatsAutoMode { get; set; } = "OncePerProject";
        [JsonProperty("FlatsAutoIntervalDays")] public int FlatsAutoIntervalDays { get; set; } = 7;

        // Site coordinates stamped in by the desktop at push time — used to warn when the
        // NINA profile's observatory location disagrees with the rig's site.
        [JsonProperty("SiteLat")] public double? SiteLat { get; set; }
        [JsonProperty("SiteLon")] public double? SiteLon { get; set; }
    }

    public class ApiImagingSystemsResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("imaging_systems")]
        public List<ImagingSystemInfo> ImagingSystems { get; set; }
    }
}
