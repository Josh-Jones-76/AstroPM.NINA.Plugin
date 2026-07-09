using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Utility;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Trigger;
using System;
using System.ComponentModel.Composition;
using System.Threading;
using System.Threading.Tasks;
using AstroPM.NINA.Plugin.Services;

namespace AstroPM.NINA.Plugin.Instructions {

    /// <summary>
    /// "Astro PM Remote Play/Pause" — add to Global Triggers. Polls the Astro PM cloud
    /// (~2 min) for the pause/play state the phone app sets for this NINA's selected
    /// imaging system. On Pause the trigger fires at the next item boundary anywhere in
    /// the sequence and HOLDS (the running exposure finishes first), acknowledging back
    /// to the cloud so the phone shows "paused — confirmed". On Play it releases.
    ///
    /// Fail-safe: only a successful poll can change the state — network loss keeps the
    /// last-known state (a paused rig stays paused, a running rig keeps running).
    /// </summary>
    [ExportMetadata("Name", "Astro PM Remote Play/Pause")]
    [ExportMetadata("Description", "Pauses/resumes the sequencer from the Astro PM phone app. Add to Global Triggers; holds at the next instruction boundary until Play is pressed remotely.")]
    [ExportMetadata("Icon", "HourglassSVG")]
    [ExportMetadata("Category", "Astro PM Tools")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class AstroPMRemotePauseTrigger : SequenceTrigger {

        [ImportingConstructor]
        public AstroPMRemotePauseTrigger() {
        }

        private AstroPMRemotePauseTrigger(AstroPMRemotePauseTrigger cloneMe) : this() {
            CopyMetaData(cloneMe);
        }

        public override void Initialize() {
            base.Initialize();
            // Continuous background poll while the sequence runs, so a pause is
            // acknowledged promptly even when the sequencer sits inside a single
            // hours-long item (e.g. "Wait until time") that never hits a boundary.
            NinaControlPoller.SequenceStarted();
        }

        public override void Teardown() {
            NinaControlPoller.SequenceStopped();
            base.Teardown();
        }

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            // Non-blocking: kicks a background refresh when the cached state is stale,
            // then answers from cache. Evaluated before every item in the sequence.
            NinaControlPoller.EnsureFresh();
            return NinaControlPoller.IsPauseRequested;
        }

        public override bool ShouldTriggerAfter(ISequenceItem previousItem, ISequenceItem nextItem) {
            return false;
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            var pausedSince = DateTime.Now;
            var requestedBy = NinaControlPoller.LastRequestedBy;
            Logger.Info($"AstroPM | Remote PAUSE engaged{(string.IsNullOrEmpty(requestedBy) ? "" : $" (requested by {requestedBy})")} — holding sequencer");

            bool acked = await NinaControlPoller.AckAsync("paused").ConfigureAwait(false);

            try {
                while (!token.IsCancellationRequested) {
                    progress?.Report(new ApplicationStatus {
                        Status = $"Paused remotely via Astro PM since {pausedSince:HH:mm} — waiting for Play"
                    });

                    // While holding, check more often than the idle poll so resume is snappy.
                    await Task.Delay(TimeSpan.FromSeconds(30), token).ConfigureAwait(false);

                    var (ok, state) = await NinaControlPoller.RefreshNowAsync().ConfigureAwait(false);
                    if (ok && state == "play") {
                        break;
                    }
                    if (ok) {
                        // Heartbeat: keep the ack timestamp fresh so the phone can see the rig is alive.
                        await NinaControlPoller.AckAsync("paused").ConfigureAwait(false);
                        acked = true;
                    }
                }
            } catch (OperationCanceledException) {
                Logger.Info("AstroPM | Remote pause hold cancelled locally (sequence stopped)");
                throw;
            } finally {
                progress?.Report(new ApplicationStatus { Status = string.Empty });
                if (acked && !token.IsCancellationRequested) {
                    await NinaControlPoller.AckAsync("playing").ConfigureAwait(false);
                }
            }

            Logger.Info("AstroPM | Remote PLAY received — sequencer resuming");
        }

        public override object Clone() => new AstroPMRemotePauseTrigger(this);
        public override string ToString() => "Astro PM Remote Play/Pause";
    }
}
