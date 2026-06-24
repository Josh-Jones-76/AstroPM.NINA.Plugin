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
        [JsonProperty("MosaicPanelPreference")] public bool MosaicPanelPreference { get; set; } = true;
        [JsonProperty("DitherEnabled")] public bool DitherEnabled { get; set; } = true;
        [JsonProperty("DitherEvery")] public int DitherEvery { get; set; } = 3;
        [JsonProperty("FilterSwitchEnabled")] public bool FilterSwitchEnabled { get; set; } = true;
        [JsonProperty("FilterSwitchCount")] public int FilterSwitchCount { get; set; } = 20;
        [JsonProperty("FilterSwitchTolerance")] public double FilterSwitchTolerance { get; set; } = 0.5;
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
