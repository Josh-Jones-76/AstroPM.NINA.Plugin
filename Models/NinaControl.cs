using Newtonsoft.Json;

namespace AstroPM.NINA.Plugin.Models
{
    /// <summary>
    /// Remote play/pause state for one imaging system, from nina_control.php.
    /// "state" is what the user requested (play|pause); the ack_* fields are what
    /// this plugin reported actually happening (playing|paused).
    /// </summary>
    public class NinaControlInfo
    {
        [JsonProperty("system_name")]
        public string SystemName { get; set; }

        [JsonProperty("state")]
        public string State { get; set; }

        [JsonProperty("requested_by")]
        public string RequestedBy { get; set; }

        [JsonProperty("requested_at")]
        public string RequestedAt { get; set; }

        [JsonProperty("ack_state")]
        public string AckState { get; set; }

        [JsonProperty("ack_by")]
        public string AckBy { get; set; }

        [JsonProperty("ack_at")]
        public string AckAt { get; set; }
    }

    public class ApiNinaControlResponse
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; }

        [JsonProperty("control")]
        public NinaControlInfo Control { get; set; }
    }
}
