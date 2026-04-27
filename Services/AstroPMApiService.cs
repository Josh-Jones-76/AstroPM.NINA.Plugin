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
            var handler = new HttpClientHandler
            {
                UseCookies = true,
                CookieContainer = new System.Net.CookieContainer()
            };
            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("AstroPM-NINA/1.0");
            _httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/json");
            _httpClient.DefaultRequestHeaders.Add("X-ASG-License", "ASG-2026-SYNC-a1b2c3d4");
        }

        private const string ApiUrl = "https://asgastronomy.com/api/project_sync.php";

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
                return (false, $"Connection failed: {ex.Message}", 0);
            }
            catch (Exception ex)
            {
                return (false, $"Error: {ex.Message}", 0);
            }
        }
    }
}
