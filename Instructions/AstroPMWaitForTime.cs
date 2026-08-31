using Newtonsoft.Json;
using NINA.Astrometry;
using NINA.Core.Locale;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Utility.DateTimeProvider;
using NINA.Sequencer.Validations;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstroPM.NINA.Plugin.Services;

namespace AstroPM.NINA.Plugin.Instructions {

    /// <summary>
    /// "Astro PM Wait for Time" — drop-in replacement for NINA's Wait for Time with the
    /// same time sources (fixed time, sunset, dusk, dawn, sunrise, meridian, ...) and
    /// minutes offset, plus two differences:
    ///
    /// 1. Remote Play/Pause aware: a pause from the Astro PM phone app engages HERE,
    ///    inside the wait, instead of silently waiting for the next instruction
    ///    boundary. The hold is visible in the sequencer status and acknowledged to
    ///    the phone; Play releases it.
    ///
    /// 2. Resume-safe: the target time is re-anchored to the current reference date
    ///    when the wait starts and again after every remote hold, so a time baked on
    ///    a previous night can never read as "already passed" and skip the wait.
    /// </summary>
    [ExportMetadata("Name", "Astro PM Wait for Time")]
    [ExportMetadata("Description", "Waits for the chosen time exactly like NINA's Wait for Time, but honors the Astro PM remote Play/Pause: pausing from the phone holds immediately inside the wait, and resuming re-computes the target time for the current night instead of skipping ahead.")]
    [ExportMetadata("Icon", "ClockSVG")]
    [ExportMetadata("Category", "Astro PM Tools")]
    [Export(typeof(ISequenceItem))]
    [JsonObject(MemberSerialization.OptIn)]
    public class AstroPMWaitForTime : SequenceItem, IValidatable {
        private IList<IDateTimeProvider> dateTimeProviders;
        private int hours;
        private int minutes;
        private int minutesOffset;
        private int seconds;
        private IDateTimeProvider selectedProvider;
        private bool isRemotePaused;

        [ImportingConstructor]
        public AstroPMWaitForTime(IList<IDateTimeProvider> dateTimeProviders) {
            DateTime = new SystemDateTime();
            this.DateTimeProviders = dateTimeProviders;
            this.SelectedProvider = DateTimeProviders?.FirstOrDefault();
        }

        public AstroPMWaitForTime(IList<IDateTimeProvider> dateTimeProviders, IDateTimeProvider selectedProvider) {
            DateTime = new SystemDateTime();
            this.DateTimeProviders = dateTimeProviders;
            this.SelectedProvider = selectedProvider;
        }

        private AstroPMWaitForTime(AstroPMWaitForTime cloneMe) : this(cloneMe.DateTimeProviders, cloneMe.SelectedProvider) {
            CopyMetaData(cloneMe);
        }

        public override object Clone() {
            return new AstroPMWaitForTime(this) {
                Hours = Hours,
                Minutes = Minutes,
                Seconds = Seconds,
                MinutesOffset = MinutesOffset
            };
        }

        private IList<string> issues = new List<string>();

        public IList<string> Issues {
            get => issues;
            set {
                issues = value;
                RaisePropertyChanged();
            }
        }

        public bool Validate() {
            var i = new List<string>();
            if (HasFixedTimeProvider) {
                var referenceDate = NighttimeCalculator.GetReferenceDate(DateTime.Now);
                if (lastReferenceDate != referenceDate) {
                    UpdateTime();
                }
            }
            if (!timeDeterminedSuccessfully) {
                i.Add(Loc.Instance["LblSelectedTimeSourceInvalid"]);
            }

            Issues = i;
            return i.Count == 0;
        }

        public IList<IDateTimeProvider> DateTimeProviders {
            get => dateTimeProviders;
            set {
                dateTimeProviders = value;
                RaisePropertyChanged();
            }
        }

        public bool HasFixedTimeProvider => selectedProvider != null && !(selectedProvider is global::NINA.Sequencer.Utility.DateTimeProvider.TimeProvider);

        /// <summary>True while a remote pause from the phone app is holding this wait.</summary>
        public bool IsRemotePaused {
            get => isRemotePaused;
            private set {
                isRemotePaused = value;
                RaisePropertyChanged();
            }
        }

        [JsonProperty]
        public int Hours {
            get => hours;
            set {
                hours = value;
                RaisePropertyChanged();
            }
        }

        [JsonProperty]
        public int Minutes {
            get => minutes;
            set {
                minutes = value;
                RaisePropertyChanged();
            }
        }

        [JsonProperty]
        public int MinutesOffset {
            get => minutesOffset;
            set {
                minutesOffset = value;
                UpdateTime();
                RaisePropertyChanged();
            }
        }

        [JsonProperty]
        public int Seconds {
            get => seconds;
            set {
                seconds = value;
                RaisePropertyChanged();
            }
        }

        [JsonProperty]
        public IDateTimeProvider SelectedProvider {
            get => selectedProvider;
            set {
                selectedProvider = value;
                if (selectedProvider != null) {
                    UpdateTime();
                    RaisePropertyChanged();
                    RaisePropertyChanged(nameof(HasFixedTimeProvider));
                }
            }
        }

        private bool timeDeterminedSuccessfully;
        private DateTime lastReferenceDate;

        private void UpdateTime() {
            try {
                lastReferenceDate = NighttimeCalculator.GetReferenceDate(DateTime.Now);
                if (HasFixedTimeProvider) {
                    var t = SelectedProvider.GetDateTime(this) + TimeSpan.FromMinutes(MinutesOffset);
                    Hours = t.Hour;
                    Minutes = t.Minute;
                    Seconds = t.Second;
                }
                timeDeterminedSuccessfully = true;
            } catch (Exception) {
                timeDeterminedSuccessfully = false;
                Validate();
            }
        }

        public override void AfterParentChanged() {
            UpdateTime();
        }

        public ICustomDateTime DateTime { get; set; }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            // Re-anchor the provider time to the current reference date. Without this
            // a time baked on a previous night can read as "already passed" and the
            // whole wait is skipped.
            UpdateTime();

            try {
                while (!token.IsCancellationRequested) {
                    if (NinaControlPoller.IsPauseRequested) {
                        await HoldWhilePausedAsync(progress, token).ConfigureAwait(false);
                        // Re-anchor after a potentially hours-long hold.
                        UpdateTime();
                        continue;
                    }

                    var remaining = GetEstimatedDuration();
                    if (remaining <= TimeSpan.Zero) break;

                    progress?.Report(new ApplicationStatus { Status = FormatCountdown(remaining) });

                    // Self-throttled cloud poll (~2 min) so a pause engages here even
                    // when no Remote Play/Pause trigger is running a background poll.
                    NinaControlPoller.EnsureFresh();

                    var slice = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
                    await Task.Delay(slice, token).ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
            } finally {
                progress?.Report(new ApplicationStatus { Status = string.Empty });
            }
        }

        private async Task HoldWhilePausedAsync(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var pausedSince = DateTime.Now;
            var requestedBy = NinaControlPoller.LastRequestedBy;
            Logger.Info($"AstroPM | Wait for Time — remote PAUSE engaged{(string.IsNullOrEmpty(requestedBy) ? "" : $" (requested by {requestedBy})")} — holding inside wait");
            IsRemotePaused = true;
            await NinaControlPoller.AckAsync("paused").ConfigureAwait(false);

            try {
                while (!token.IsCancellationRequested) {
                    progress?.Report(new ApplicationStatus {
                        Status = $"Paused remotely via Astro PM since {pausedSince:HH:mm} — waiting for Play"
                    });

                    await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);

                    var (ok, state) = await NinaControlPoller.RefreshNowAsync().ConfigureAwait(false);
                    if (ok && state == "play") {
                        break;
                    }
                    if (ok) {
                        // Heartbeat: keep the ack timestamp fresh so the phone can see the rig is alive.
                        await NinaControlPoller.AckAsync("paused").ConfigureAwait(false);
                    }
                }
            } catch (OperationCanceledException) {
                Logger.Info("AstroPM | Wait for Time — remote pause hold cancelled locally (sequence stopped)");
                throw;
            } finally {
                IsRemotePaused = false;
            }

            await NinaControlPoller.AckAsync("playing").ConfigureAwait(false);
            Logger.Info("AstroPM | Wait for Time — remote PLAY received, resuming wait");
        }

        private static string FormatCountdown(TimeSpan remaining) {
            var status = Loc.Instance["LblWaiting"];
            if (remaining.Hours > 0) {
                return $"{status} {remaining.Hours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
            }
            if (remaining.Minutes > 0) {
                return $"{status} {remaining.Minutes:D2}:{remaining.Seconds:D2}";
            }
            return $"{status} {remaining.Seconds} s";
        }

        public override TimeSpan GetEstimatedDuration() {
            var now = DateTime.Now;
            var then = new DateTime(now.Year, now.Month, now.Day, Hours, Minutes, Seconds);

            if (SelectedProvider != null) {
                var rollover = SelectedProvider.GetRolloverTime(this);
                var timeOnlyNow = TimeOnly.FromDateTime(now);
                var timeOnlyThen = TimeOnly.FromDateTime(then);

                if (timeOnlyNow < rollover && timeOnlyThen >= rollover) {
                    then = then.AddDays(-1);
                }

                if (timeOnlyNow >= rollover && timeOnlyThen < rollover) {
                    then = then.AddDays(1);
                }
            }

            var diff = then - DateTime.Now;
            if (diff < TimeSpan.Zero) {
                return TimeSpan.Zero;
            } else {
                return diff;
            }
        }

        public override string ToString() {
            return $"Category: {Category}, Item: {nameof(AstroPMWaitForTime)}, Time: {Hours}:{Minutes}:{Seconds}h, Offset: {MinutesOffset}";
        }
    }
}
