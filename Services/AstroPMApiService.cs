using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AstroPM.NINA.Plugin.Models;
using Newtonsoft.Json;

namespace AstroPM.NINA.Plugin.Services
{
    public class AstroPMApiService
    {
        private static readonly HttpClient _httpClient;

        static AstroPMApiService()
        {
            // No cookies (the API doesn't use them) — keeps the handler minimal.
            var handler = new HttpClientHandler
            {
                UseCookies = false
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            // Use a conventional User-Agent format (with URL) so server logs and
            // WAFs can identify legitimate traffic. Short non-descriptive UAs
            // sometimes trip generic ModSecurity rules on shared hosting.
            // The REAL assembly version goes in the UA — the server-side API log uses it
            // to tell which plugin build a client runs (it was hardcoded 1.0.0.4 for years,
            // which made every install look ancient).
            var pluginVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
                $"AstroPM.NINA.Plugin/{pluginVersion} (+https://github.com/Josh-Jones-76/AstroPM.NINA.Plugin)");
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        }

        private const string ApiUrl = "https://astro-pm.com/api/project_sync.php";
        private const string ImagingSystemsApiUrl = "https://astro-pm.com/api/imaging_systems_sync.php";
        private const string NinaControlApiUrl = "https://astro-pm.com/api/nina_control.php";

        /// <summary>
        /// Fetch the remote play/pause state for one imaging system (the "Astro PM
        /// Remote Play/Pause" trigger polls this).
        /// </summary>
        public async Task<ApiNinaControlResponse> GetNinaControlAsync(string syncToken, string systemName, CancellationToken ct = default)
        {
            var payload = new Dictionary<string, object>
            {
                ["sync_token"] = syncToken,
                ["action"] = "get",
                ["system_name"] = systemName
            };
            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(NinaControlApiUrl, content, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            var trimmed = body?.TrimStart();
            if (string.IsNullOrEmpty(trimmed) || !trimmed.StartsWith("{"))
            {
                return new ApiNinaControlResponse
                {
                    Success = false,
                    Message = $"HTTP {(int)response.StatusCode}: Server returned non-JSON response."
                };
            }
            return JsonConvert.DeserializeObject<ApiNinaControlResponse>(body);
        }

        /// <summary>
        /// Report back what the trigger is actually doing (paused|playing) so the phone
        /// can show "confirmed by <machine>". Best-effort; failures are non-fatal.
        /// </summary>
        public async Task<bool> AckNinaControlAsync(string syncToken, string systemName, string ackState, string ackBy, CancellationToken ct = default)
        {
            var payload = new Dictionary<string, object>
            {
                ["sync_token"] = syncToken,
                ["action"] = "ack",
                ["system_name"] = systemName,
                ["ack_state"] = ackState,
                ["ack_by"] = ackBy
            };
            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(NinaControlApiUrl, content, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }

        /// <summary>
        /// List the imaging systems (rigs) for the sync token, including their site/telescope/camera
        /// names and (reserved) simulator settings.
        /// </summary>
        public async Task<ApiImagingSystemsResponse> ListImagingSystemsAsync(string syncToken, CancellationToken ct = default)
        {
            var payload = new Dictionary<string, object>
            {
                ["sync_token"] = syncToken,
                ["action"] = "list"
            };
            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(ImagingSystemsApiUrl, content, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            var trimmed = body?.TrimStart();
            if (string.IsNullOrEmpty(trimmed) || (!trimmed.StartsWith("{") && !trimmed.StartsWith("[")))
            {
                return new ApiImagingSystemsResponse
                {
                    Success = false,
                    Message = $"HTTP {(int)response.StatusCode}: Server returned non-JSON response."
                };
            }
            if (!response.IsSuccessStatusCode)
            {
                return new ApiImagingSystemsResponse
                {
                    Success = false,
                    Message = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}"
                };
            }
            return JsonConvert.DeserializeObject<ApiImagingSystemsResponse>(body);
        }

        /// <summary>
        /// Reports the readout-mode list enumerated from the live camera as ADVISORY data on the
        /// imaging system's cloud row (the desktop app surfaces it as a suggestion; nothing
        /// schedules from it). Fire-and-forget semantics: returns true only when the server
        /// confirms it stored the report.
        /// </summary>
        public async Task<bool> ReportCameraModesAsync(string syncToken, string systemName, string cameraName, IList<string> modes, CancellationToken ct = default)
        {
            var payload = new Dictionary<string, object>
            {
                ["sync_token"] = syncToken,
                ["action"] = "report_camera_modes",
                ["name"] = systemName,
                ["camera_name"] = cameraName ?? "",
                ["modes"] = modes
            };
            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(ImagingSystemsApiUrl, content, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return false;
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            try
            {
                var parsed = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                return parsed != null
                    && parsed.TryGetValue("stored", out var stored)
                    && stored is bool b && b;
            }
            catch { return false; }
        }

        /// <summary>
        /// List all targets matching the sync token, optionally filtered by status.
        /// </summary>
        public async Task<ApiListResponse> ListTargetsAsync(string syncToken, string statusFilter = null, CancellationToken ct = default)
        {
            var payload = new Dictionary<string, object>
            {
                ["sync_token"] = syncToken,
                ["action"] = "list"
            };

            if (!string.IsNullOrWhiteSpace(statusFilter))
            {
                payload["status"] = statusFilter;
            }

            var json = JsonConvert.SerializeObject(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(ApiUrl, content, ct).ConfigureAwait(false);
            var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

            // Guard against non-JSON responses (bot protection, error pages, etc.)
            var trimmed = responseBody?.TrimStart();
            if (string.IsNullOrEmpty(trimmed) || !trimmed.StartsWith("{") && !trimmed.StartsWith("["))
            {
                return new ApiListResponse
                {
                    Success = false,
                    Message = $"HTTP {(int)response.StatusCode}: Server returned non-JSON response."
                };
            }

            if (!response.IsSuccessStatusCode)
            {
                try
                {
                    var errorResult = JsonConvert.DeserializeObject<ApiListResponse>(responseBody);
                    return errorResult ?? new ApiListResponse
                    {
                        Success = false,
                        Message = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}"
                    };
                }
                catch
                {
                    return new ApiListResponse
                    {
                        Success = false,
                        Message = $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}"
                    };
                }
            }

            return JsonConvert.DeserializeObject<ApiListResponse>(responseBody);
        }

        /// <summary>
        /// Validate that a sync token is valid and returns targets.
        /// </summary>
        public async Task<(bool IsValid, string Message, int TargetCount)> ValidateTokenAsync(string syncToken, CancellationToken ct = default)
        {
            try
            {
                var result = await ListTargetsAsync(syncToken, null, ct).ConfigureAwait(false);
                if (result.Success)
                {
                    int count = result.Targets?.Count ?? 0;
                    return (true, $"Connected. {count} target(s) found.", count);
                }
                return (false, result.Message ?? "Unknown error.", 0);
            }
            catch (TaskCanceledException)
            {
                return (false, "Request timed out.", 0);
            }
            catch (HttpRequestException ex)
            {
                // "Actively refused" / connection-refused errors are almost always
                // local: antivirus, Windows Firewall, or a corporate proxy blocking
                // NINA.exe. Surface actionable guidance so the user has somewhere to start.
                var msg = ex.Message ?? string.Empty;
                if (msg.IndexOf("actively refused", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("connection refused", StringComparison.OrdinalIgnoreCase) >= 0
                    || msg.IndexOf("unreachable", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return (false,
                        "Could not reach astro-pm.com. This is usually caused by antivirus, " +
                        "Windows Firewall, or a corporate network blocking NINA.exe. " +
                        "Try whitelisting NINA.exe in your security software, or run NINA as administrator. " +
                        $"(Underlying error: {ex.Message})",
                        0);
                }
                return (false, $"Connection failed: {ex.Message}", 0);
            }
            catch (Exception ex)
            {
                return (false, $"Error: {ex.Message}", 0);
            }
        }
    }
}
