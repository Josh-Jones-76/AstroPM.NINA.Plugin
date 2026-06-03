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

            var hasRemaining = cache.Targets.Any(t =>
                t.Panels != null && t.Panels.Any(p =>
                    p.ExposureSets != null && p.ExposureSets.Any(es => es.Remaining > 0)));

            if (hasRemaining) {
                var tis = FindTargetInstructionSet();
                // Only reset when tonight's session is truly over. The watchdog calls
                // Check() continuously — resetting mid-session nukes _scheduleBuilt,
                // which causes BuildSchedule to re-run after midnight using DateTime.Today
                // (now tomorrow), losing any remaining blocks for tonight.
                if (tis != null && !tis.HasBlocksRemaining) {
                    tis.ResetForNewNight();
                }
            }

            return hasRemaining;
        }

        private TargetInstructionSet FindTargetInstructionSet() {
            var container = Parent;
            while (container != null) {
                var found = SearchContainer(container);
                if (found != null) return found;
                container = container.Parent;
            }
            return null;
        }

        private TargetInstructionSet SearchContainer(ISequenceContainer container) {
            foreach (var item in container.GetItemsSnapshot()) {
                if (item is TargetInstructionSet tis) return tis;
                if (item is ISequenceContainer child) {
                    var found = SearchContainer(child);
                    if (found != null) return found;
                }
            }
            return null;
        }

        public override object Clone() {
            return new AstroPMDailyLoopCondition(this);
        }

        public override string ToString() {
            return $"Category: Astro PM Tools, Item: {nameof(AstroPMDailyLoopCondition)}";
        }
    }
}
