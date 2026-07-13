using Newtonsoft.Json;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using System.ComponentModel.Composition;
using System.Linq;
using AstroPM.NINA.Plugin.Services;

namespace AstroPM.NINA.Plugin.Instructions {

    [ExportMetadata("Name", "Astro PM Daily Loop")]
    [ExportMetadata("Description", "Loops day-to-day while active projects have remaining exposures")]
    [ExportMetadata("Icon", "LoopSVG")]
    [ExportMetadata("Category", "Astro PM Tools")]
    [Export(typeof(ISequenceCondition))]
    [JsonObject(MemberSerialization.OptIn)]
    public class AstroPMDailyLoopCondition : SequenceCondition {

        [ImportingConstructor]
        public AstroPMDailyLoopCondition() { }

        private AstroPMDailyLoopCondition(AstroPMDailyLoopCondition copyMe) : this() {
            CopyMetaData(copyMe);
        }

        public override bool Check(ISequenceItem previousItem, ISequenceItem nextItem) {
            var cache = TargetCacheService.Load();
            if (cache == null) return true; // no cache yet — keep looping so RefreshCloudTargets can run

            // Day-to-day looping only. This condition no longer resets the instruction set:
            // the watchdog calls Check() continuously, and a reset here raced the gap between
            // blocks (HasBlocksRemaining flickers false when work completes before session end),
            // rebuilding mid-night — after midnight that builds the WRONG night. New-night
            // resets are owned by TargetInstructionSet.Execute() via IsStaleSession, which also
            // respects the pending-flats guard this path bypassed.
            return cache.Targets.Any(t =>
                t.Panels != null && t.Panels.Any(p =>
                    p.ExposureSets != null && p.ExposureSets.Any(es => es.Remaining > 0)));
        }

        public override object Clone() {
            return new AstroPMDailyLoopCondition(this);
        }

        public override string ToString() {
            return $"Category: Astro PM Tools, Item: {nameof(AstroPMDailyLoopCondition)}";
        }
    }
}
