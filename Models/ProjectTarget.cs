using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace AstroPM.NINA.Plugin.Models
{
    public class ProjectTarget
    {
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
        public List<object> Panels { get; set; }

        [JsonProperty("pushed_at")]
        public string PushedAt { get; set; }

        [JsonProperty("updated_at")]
        public string UpdatedAt { get; set; }

        /// <summary>
        /// Display-friendly RA as HH:MM:SS
        /// </summary>
        [JsonIgnore]
        public string RaDisplay
        {
            get
            {
                double totalHours = RaHours;
                int h = (int)totalHours;
                double remainder = (totalHours - h) * 60.0;
                int m = (int)remainder;
                double s = (remainder - m) * 60.0;
                return $"{h:00}h {m:00}m {s:00.0}s";
            }
        }

        /// <summary>
        /// Display-friendly Dec as DD:MM:SS
        /// </summary>
        [JsonIgnore]
        public string DecDisplay
        {
            get
            {
                double totalDeg = DecDegrees;
                string sign = totalDeg >= 0 ? "+" : "-";
                totalDeg = Math.Abs(totalDeg);
                int d = (int)totalDeg;
                double remainder = (totalDeg - d) * 60.0;
                int m = (int)remainder;
                double s = (remainder - m) * 60.0;
                return $"{sign}{d:00}d {m:00}m {s:00.0}s";
            }
        }

        /// <summary>
        /// Panel grid display string
        /// </summary>
        [JsonIgnore]
        public string PanelGridDisplay => $"{PanelRows}x{PanelColumns}";

        /// <summary>
        /// Sensor resolution display string
        /// </summary>
        [JsonIgnore]
        public string SensorDisplay =>
            CameraPixelWidth.HasValue && CameraPixelHeight.HasValue
                ? $"{CameraPixelWidth}x{CameraPixelHeight}"
                : "";
    }

    public class ApiListResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("targets")]
        public List<ProjectTarget> Targets { get; set; }
    }
}
