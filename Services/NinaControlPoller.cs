using NINA.Core.Utility;
using System;
using System.Threading.Tasks;

namespace AstroPM.NINA.Plugin.Services {

    /// <summary>
    /// Shared cached view of the cloud play/pause state for this NINA's selected
    /// imaging system. One poll every POLL_SECONDS regardless of how many sequence
    /// items ask — the trigger's ShouldTrigger answers from cache and never blocks.
    ///
    /// Fail-safe: only a successful poll updates the state, so losing the network
    /// keeps the last-known state (paused stays paused, playing stays playing).
    /// </summary>
    public static class NinaControlPoller {

        private const int PollSeconds = 120;

        private static readonly object _lock = new object();
        private static readonly AstroPMApiService _api = new AstroPMApiService();
        private static DateTime _lastPollUtc = DateTime.MinValue;
        private static string _state = "play";
        private static string _requestedBy = "";
        private static bool _refreshing;
        private static System.Threading.Timer _backgroundTimer;
        private static int _activeTriggers;
        private static bool _ackedPause;

        public static bool IsPauseRequested {
            get { lock (_lock) return _state == "pause"; }
        }

        public static string LastRequestedBy {
            get { lock (_lock) return _requestedBy; }
        }

        /// <summary>
        /// Called from the trigger's sequence lifecycle. While at least one Remote
        /// Play/Pause trigger is in a RUNNING sequence, poll continuously in the
        /// background — the sequencer may sit inside a single long item ("Wait until
        /// time") for hours without evaluating triggers, and the phone still deserves
        /// a prompt "NINA acknowledged the pause" instead of silence until dusk.
        /// A pause seen here is acked immediately: it means "received — the sequencer
        /// will hold at the next instruction boundary."
        /// </summary>
        public static void SequenceStarted() {
            lock (_lock) {
                _activeTriggers++;
                if (_backgroundTimer == null) {
                    Logger.Info("AstroPM | Remote control background poll started (sequence running)");
                    _backgroundTimer = new System.Threading.Timer(
                        async _ => await BackgroundPollAsync().ConfigureAwait(false),
                        null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(PollSeconds));
                }
            }
        }

        public static void SequenceStopped() {
            lock (_lock) {
                _activeTriggers = Math.Max(0, _activeTriggers - 1);
                if (_activeTriggers == 0 && _backgroundTimer != null) {
                    _backgroundTimer.Dispose();
                    _backgroundTimer = null;
                    _ackedPause = false;
                    Logger.Info("AstroPM | Remote control background poll stopped");
                }
            }
        }

        private static async Task BackgroundPollAsync() {
            var (ok, state) = await RefreshNowAsync().ConfigureAwait(false);
            if (!ok) return;
            if (state == "pause") {
                // Acknowledge (and heartbeat) even if the sequencer is stuck inside a
                // long wait item — the hold is guaranteed at the next boundary.
                await AckAsync("paused").ConfigureAwait(false);
                lock (_lock) _ackedPause = true;
            } else {
                bool releaseAck;
                lock (_lock) { releaseAck = _ackedPause; _ackedPause = false; }
                if (releaseAck) await AckAsync("playing").ConfigureAwait(false);
            }
        }

        /// <summary>Kick a background refresh if the cache is stale. Never blocks.</summary>
        public static void EnsureFresh() {
            lock (_lock) {
                if (_refreshing || (DateTime.UtcNow - _lastPollUtc).TotalSeconds < PollSeconds) return;
                _refreshing = true;
            }
            _ = Task.Run(async () => {
                try { await RefreshNowAsync().ConfigureAwait(false); }
                finally { lock (_lock) _refreshing = false; }
            });
        }

        /// <summary>Poll the cloud immediately. Returns (succeeded, current state).</summary>
        public static async Task<(bool ok, string state)> RefreshNowAsync() {
            var settings = AstroPMSettings.Load();
            if (settings == null
                || settings.OfflineMode
                || string.IsNullOrWhiteSpace(settings.SyncToken)
                || string.IsNullOrWhiteSpace(settings.SelectedImagingSystem)) {
                // Not configured (or vacation mode) — remote control inert, always play.
                lock (_lock) { _lastPollUtc = DateTime.UtcNow; return (false, _state); }
            }

            try {
                var resp = await _api.GetNinaControlAsync(settings.SyncToken, settings.SelectedImagingSystem).ConfigureAwait(false);
                if (resp != null && resp.Success && resp.Control != null) {
                    lock (_lock) {
                        if (_state != resp.Control.State) {
                            Logger.Info($"AstroPM | Remote control state changed: {_state} -> {resp.Control.State}");
                        }
                        _state = resp.Control.State == "pause" ? "pause" : "play";
                        _requestedBy = resp.Control.RequestedBy ?? "";
                        _lastPollUtc = DateTime.UtcNow;
                        return (true, _state);
                    }
                }
                Logger.Debug($"AstroPM | Remote control poll rejected: {resp?.Message}");
            } catch (Exception ex) {
                Logger.Debug($"AstroPM | Remote control poll failed (keeping last state '{_state}'): {ex.Message}");
            }
            lock (_lock) { _lastPollUtc = DateTime.UtcNow; return (false, _state); }
        }

        /// <summary>Best-effort acknowledgment (paused|playing) so the phone shows confirmation.</summary>
        public static async Task<bool> AckAsync(string ackState) {
            var settings = AstroPMSettings.Load();
            if (settings == null
                || string.IsNullOrWhiteSpace(settings.SyncToken)
                || string.IsNullOrWhiteSpace(settings.SelectedImagingSystem)) return false;
            try {
                return await _api.AckNinaControlAsync(
                    settings.SyncToken, settings.SelectedImagingSystem,
                    ackState, Environment.MachineName).ConfigureAwait(false);
            } catch (Exception ex) {
                Logger.Debug($"AstroPM | Remote control ack failed: {ex.Message}");
                return false;
            }
        }
    }
}
