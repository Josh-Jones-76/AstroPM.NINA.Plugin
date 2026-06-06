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

namespace AstroPM.NINA.Plugin.Instructions {

    [ExportMetadata("Name", "Before Each Exposure Instructions")]
    [ExportMetadata("Description", "Runs contained instructions before each AstroPM exposure")]
    [ExportMetadata("Icon", "CameraSVG")]
    [ExportMetadata("Category", "Astro PM Tools")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class AstroPMBeforeExposureTrigger : SequenceTrigger {

        [ImportingConstructor]
        public AstroPMBeforeExposureTrigger() {
        }

        private AstroPMBeforeExposureTrigger(AstroPMBeforeExposureTrigger cloneMe) : base(cloneMe) {
            CopyMetaData(cloneMe);
        }

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            return nextItem is AstroPMTakeExposureItem;
        }

        public override bool ShouldTriggerAfter(ISequenceItem previousItem, ISequenceItem nextItem) {
            return false;
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            Logger.Info("AstroPM | Before Exposure trigger firing");
            TriggerRunner.AttachNewParent(context);
            foreach (var item in TriggerRunner.GetItemsSnapshot()) {
                token.ThrowIfCancellationRequested();
                await item.Run(progress, token);
            }
        }

        public override object Clone() => new AstroPMBeforeExposureTrigger(this);
        public override string ToString() => "Before Each Exposure Instructions";
    }

    [ExportMetadata("Name", "After Each Exposure Instructions")]
    [ExportMetadata("Description", "Runs contained instructions after each AstroPM exposure")]
    [ExportMetadata("Icon", "CameraSVG")]
    [ExportMetadata("Category", "Astro PM Tools")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class AstroPMAfterExposureTrigger : SequenceTrigger {

        [ImportingConstructor]
        public AstroPMAfterExposureTrigger() {
        }

        private AstroPMAfterExposureTrigger(AstroPMAfterExposureTrigger cloneMe) : base(cloneMe) {
            CopyMetaData(cloneMe);
        }

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            return false;
        }

        public override bool ShouldTriggerAfter(ISequenceItem previousItem, ISequenceItem nextItem) {
            return previousItem is AstroPMTakeExposureItem;
        }

        public override async Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            Logger.Info("AstroPM | After Exposure trigger firing");
            TriggerRunner.AttachNewParent(context);
            foreach (var item in TriggerRunner.GetItemsSnapshot()) {
                token.ThrowIfCancellationRequested();
                await item.Run(progress, token);
            }
        }

        public override object Clone() => new AstroPMAfterExposureTrigger(this);
        public override string ToString() => "After Each Exposure Instructions";
    }

    [ExportMetadata("Name", "Before Target Change Instructions")]
    [ExportMetadata("Description", "Runs contained instructions before AstroPM slews to a new target")]
    [ExportMetadata("Icon", "SlewToRaDecSVG")]
    [ExportMetadata("Category", "Astro PM Tools")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class AstroPMBeforeTargetTrigger : SequenceTrigger {

        private string _lastTargetName;

        [ImportingConstructor]
        public AstroPMBeforeTargetTrigger() {
        }

        private AstroPMBeforeTargetTrigger(AstroPMBeforeTargetTrigger cloneMe) : base(cloneMe) {
            CopyMetaData(cloneMe);
        }

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            return false;
        }

        public override bool ShouldTriggerAfter(ISequenceItem previousItem, ISequenceItem nextItem) {
            return false;
        }

        internal async Task FireIfNeeded(TargetBlock block, IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (TriggerRunner.GetItemsSnapshot().Count == 0) return;

            bool isNewTarget = _lastTargetName != null && _lastTargetName != block.TargetName;
            bool isFirstTarget = _lastTargetName == null;

            if (isFirstTarget || isNewTarget) {
                Logger.Info($"AstroPM | Before Target trigger firing: {block.TargetName}");
                if (Parent != null) TriggerRunner.AttachNewParent(Parent);
                foreach (var item in TriggerRunner.GetItemsSnapshot()) {
                    token.ThrowIfCancellationRequested();
                    await item.Run(progress, token);
                }
            }
            _lastTargetName = block.TargetName;
        }

        public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            return Task.CompletedTask;
        }

        public override object Clone() => new AstroPMBeforeTargetTrigger(this);
        public override string ToString() => "Before Target Change Instructions";
    }

    [ExportMetadata("Name", "After Target Change Instructions")]
    [ExportMetadata("Description", "Runs contained instructions after AstroPM finishes a target block")]
    [ExportMetadata("Icon", "SlewToRaDecSVG")]
    [ExportMetadata("Category", "Astro PM Tools")]
    [Export(typeof(ISequenceTrigger))]
    [JsonObject(MemberSerialization.OptIn)]
    public class AstroPMAfterTargetTrigger : SequenceTrigger {

        [ImportingConstructor]
        public AstroPMAfterTargetTrigger() {
        }

        private AstroPMAfterTargetTrigger(AstroPMAfterTargetTrigger cloneMe) : base(cloneMe) {
            CopyMetaData(cloneMe);
        }

        public override bool ShouldTrigger(ISequenceItem previousItem, ISequenceItem nextItem) {
            return false;
        }

        public override bool ShouldTriggerAfter(ISequenceItem previousItem, ISequenceItem nextItem) {
            return false;
        }

        internal async Task Fire(TargetBlock block, IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (TriggerRunner.GetItemsSnapshot().Count == 0) return;

            Logger.Info($"AstroPM | After Target trigger firing: {block.TargetName}");
            if (Parent != null) TriggerRunner.AttachNewParent(Parent);
            foreach (var item in TriggerRunner.GetItemsSnapshot()) {
                token.ThrowIfCancellationRequested();
                await item.Run(progress, token);
            }
        }

        public override Task Execute(ISequenceContainer context, IProgress<ApplicationStatus> progress, CancellationToken token) {
            return Task.CompletedTask;
        }

        public override object Clone() => new AstroPMAfterTargetTrigger(this);
        public override string ToString() => "After Target Change Instructions";
    }
}
