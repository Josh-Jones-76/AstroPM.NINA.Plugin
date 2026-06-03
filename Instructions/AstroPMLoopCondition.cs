using Newtonsoft.Json;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.SequenceItem;
using System.ComponentModel.Composition;
using System.Linq;

namespace AstroPM.NINA.Plugin.Instructions {

    [ExportMetadata("Name", "Astro PM Nightly Loop")]
    [ExportMetadata("Description", "Loops while tonight's Astro PM schedule has remaining target blocks")]
    [ExportMetadata("Icon", "LoopSVG")]
    [ExportMetadata("Category", "Astro PM Tools")]
    [Export(typeof(ISequenceCondition))]
    [JsonObject(MemberSerialization.OptIn)]
    public class AstroPMLoopCondition : SequenceCondition {

        [ImportingConstructor]
        public AstroPMLoopCondition() { }

        private AstroPMLoopCondition(AstroPMLoopCondition copyMe) : this() {
            CopyMetaData(copyMe);
        }

        public override bool Check(ISequenceItem previousItem, ISequenceItem nextItem) {
            var instruction = FindTargetInstructionSet();
            if (instruction == null) return false;
            return instruction.HasBlocksRemaining;
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
            return new AstroPMLoopCondition(this);
        }

        public override string ToString() {
            return $"Category: Astro PM Tools, Item: {nameof(AstroPMLoopCondition)}";
        }
    }
}
