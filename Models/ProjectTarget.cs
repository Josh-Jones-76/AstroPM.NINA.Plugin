using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace AstroPM.NINA.Plugin.Models {

    public class ProjectTarget {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("sync_token")]
        public string SyncToken { get; set; }

        [JsonProperty("project_name")]
        public string ProjectName { get; set; }

        [JsonProperty("target_name")]
        public string TargetName { get; set; }

        [JsonProperty("ra_hours")]
        public double RaHours { get; set; }

        [JsonProperty("dec_degrees")]
        public double DecDegrees { get; set; }

        [JsonProperty("rotation_deg")]
        public double RotationDeg { get; set; }

        [JsonProperty("panel_rows")]
        public int PanelRows { get; set; }

        [JsonProperty("panel_columns")]
        public int PanelColumns { get; set; }

        [JsonProperty("panel_overlap_percent")]
        public double PanelOverlapPercent { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("telescope_name")]
        public string TelescopeName { get; set; }

        [JsonProperty("telescope_focal_length_mm")]
        public double? TelescopeFocalLengthMm { get; set; }

        [JsonProperty("camera_name")]
        public string CameraName { get; set; }

        [JsonProperty("camera_pixel_width")]
        public int? CameraPixelWidth { get; set; }

        [JsonProperty("camera_pixel_height")]
        public int? CameraPixelHeight { get; set; }

        [JsonProperty("camera_pixel_size_um")]
        public double? CameraPixelSizeUm { get; set; }

        [JsonProperty("location_name")]
        public string LocationName { get; set; }

        [JsonProperty("panels")]
        public List<PanelData> Panels { get; set; } = new List<PanelData>();

        [JsonProperty("constraints")]
        public ConstraintsData Constraints { get; set; }

        [JsonProperty("pushed_at")]
        public string PushedAt { get; set; }

        [JsonProperty("updated_at")]
        public string UpdatedAt { get; set; }

        [JsonIgnore]
        public string RaDisplay {
            get {
                int h = (int)RaHours;
                double remainder = (RaHours - h) * 60.0;
                int m = (int)remainder;
                double s = (remainder - m) * 60.0;
                return $"{h:00}h {m:00}m {s:00.0}s";
            }
        }

        [JsonIgnore]
        public string DecDisplay {
            get {
                string sign = DecDegrees >= 0 ? "+" : "-";
                double abs = Math.Abs(DecDegrees);
                int d = (int)abs;
                double remainder = (abs - d) * 60.0;
                int m = (int)remainder;
                double s = (remainder - m) * 60.0;
                return $"{sign}{d:00}d {m:00}m {s:00.0}s";
            }
        }

        [JsonIgnore]
        public string PanelGridDisplay => $"{PanelRows}x{PanelColumns}";

        [JsonIgnore]
        public string SensorDisplay =>
            CameraPixelWidth.HasValue && CameraPixelHeight.HasValue
                ? $"{CameraPixelWidth}x{CameraPixelHeight}"
                : "";

        public ObservingConstraints ToObservingConstraints() {
            var c = new ObservingConstraints();
            if (Constraints == null) return c;

            c.SunAltitudeThreshold = Constraints.TwilightTypeIndex switch {
                1 => -12.0,
                2 => -6.0,
                _ => -18.0,
            };
            c.MinTargetAltitude = Constraints.MinTargetAltitude;
            c.MinTimeOnTargetHrs = Constraints.MinTimeOnTargetHrs;
            c.MoonAvoidanceEnabled = Constraints.MoonAvoidanceEnabled;
            c.MinMoonSeparationDeg = Constraints.MoonSeparationDeg;
            c.MoonAvoidanceWidthDays = Constraints.MoonAvoidanceWidthDays;
            c.MoonRelaxScale = Constraints.MoonRelaxScale;
            c.MinMoonAltitude = Constraints.MoonMinAltitude;
            c.MaxMoonAltitude = Constraints.MoonMaxAltitude;
            return c;
        }
    }

    public class PanelData {
        [JsonProperty("panel_index")]
        public int PanelIndex { get; set; }

        [JsonProperty("label")]
        public string Label { get; set; } = "";

        [JsonProperty("ra_hours")]
        public double RaHours { get; set; }

        [JsonProperty("dec_degrees")]
        public double DecDegrees { get; set; }

        [JsonProperty("rotation_deg")]
        public double RotationDeg { get; set; }

        [JsonProperty("exposure_sets")]
        public List<ExposureSetData> ExposureSets { get; set; } = new List<ExposureSetData>();
    }

    public class ExposureSetData {
        [JsonProperty("filter_name")]
        public string FilterName { get; set; } = "Unknown";

        [JsonProperty("exposure_length_sec")]
        public double ExposureLengthSec { get; set; }

        [JsonProperty("planned_count")]
        public int PlannedCount { get; set; }

        [JsonProperty("acquired_count")]
        public int AcquiredCount { get; set; }

        [JsonProperty("accepted_count")]
        public int AcceptedCount { get; set; }

        [JsonProperty("gain")]
        public int Gain { get; set; }

        [JsonProperty("offset")]
        public int Offset { get; set; }

        [JsonProperty("binning_x")]
        public int BinningX { get; set; } = 1;

        [JsonProperty("binning_y")]
        public int BinningY { get; set; } = 1;

        [JsonProperty("avoid_lunar")]
        public bool AvoidLunar { get; set; }

        [JsonProperty("moon_avoidance_profile")]
        public MoonAvoidanceProfileData MoonAvoidanceProfile { get; set; }

        [JsonIgnore]
        public bool HasMoonAvoidance => AvoidLunar || MoonAvoidanceProfile != null;

        [JsonIgnore]
        public int Remaining => Math.Max(0, PlannedCount - AcceptedCount);
    }

    public class MoonAvoidanceProfileData {
        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("moon_separation_deg")]
        public double MoonSeparationDeg { get; set; } = 60.0;

        [JsonProperty("moon_avoidance_width_days")]
        public double MoonAvoidanceWidthDays { get; set; } = 7.0;

        [JsonProperty("moon_relax_scale")]
        public double MoonRelaxScale { get; set; } = 0.0;

        [JsonProperty("moon_min_altitude")]
        public double MoonMinAltitude { get; set; } = -15.0;

        [JsonProperty("moon_max_altitude")]
        public double MoonMaxAltitude { get; set; } = 5.0;

        [JsonProperty("max_moon_illumination_pct")]
        public double MaxMoonIlluminationPct { get; set; } = 40.0;
    }

    public class ConstraintsData {
        [JsonProperty("twilight_type_index")]
        public int TwilightTypeIndex { get; set; }

        [JsonProperty("min_target_altitude")]
        public double MinTargetAltitude { get; set; } = 30.0;

        [JsonProperty("min_time_on_target_hrs")]
        public double MinTimeOnTargetHrs { get; set; } = 1.0;

        [JsonProperty("moon_avoidance_enabled")]
        public bool MoonAvoidanceEnabled { get; set; }

        [JsonProperty("moon_separation_deg")]
        public double MoonSeparationDeg { get; set; } = 60.0;

        [JsonProperty("moon_avoidance_width_days")]
        public double MoonAvoidanceWidthDays { get; set; } = 7.0;

        [JsonProperty("moon_relax_scale")]
        public double MoonRelaxScale { get; set; }

        [JsonProperty("moon_min_altitude")]
        public double MoonMinAltitude { get; set; } = -15.0;

        [JsonProperty("moon_max_altitude")]
        public double MoonMaxAltitude { get; set; } = 5.0;
    }

    public class ApiListResponse {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("targets")]
        public List<ProjectTarget> Targets { get; set; }
    }
}
