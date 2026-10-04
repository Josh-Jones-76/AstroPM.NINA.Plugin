using Newtonsoft.Json;
using NINA.Core.Model;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Core.Utility.Notification;
using NINA.Core.Utility.WindowService;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Model;
using NINA.PlateSolving.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Container;
using NINA.Sequencer.Container.ExecutionStrategy;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Platesolving;
using NINA.Sequencer.Trigger.Platesolving;
using NINA.Astrometry;
using NINA.Astrometry.Interfaces;
using AstroPM.NINA.Plugin.Models;
using AstroPM.NINA.Plugin.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AstroPM.NINA.Plugin.Instructions {

    public class TargetBlock {
        public string TargetName { get; set; } = "";
        public double RaHours { get; set; }
        public double DecDegrees { get; set; }
        public double RotationDeg { get; set; }
        public DateTime UtcStart { get; set; }
        public DateTime UtcEnd { get; set; }
        public List<SimLogEntry> Entries { get; set; } = new List<SimLogEntry>();
        public TargetProfile Profile { get; set; }
    }

    public class BlockSummary : BaseINPC {
        public int Number { get; set; }
        public string TargetName { get; set; } = "";
        public string TimeRange { get; set; } = "";
        public int PlannedCount { get; set; }
        public string Coords { get; set; } = "";
        public double RaHours { get; set; }
        public double DecDegrees { get; set; }
        public double RotationDeg { get; set; }

        private int _capturedCount;
        public int CapturedCount {
            get => _capturedCount;
            set { _capturedCount = value; RaisePropertyChanged(); RaisePropertyChanged(nameof(SubCountDisplay)); }
        }

        public string SubCountDisplay => $"{CapturedCount} / {PlannedCount} subs";

        private string _status = "";
        public string Status {
            get => _status;
            set { _status = value; RaisePropertyChanged(); }
        }

        public string MeridianInfo { get; set; } = "";

        private bool _isCurrent;
        public bool IsCurrent {
            get => _isCurrent;
            set { _isCurrent = value; RaisePropertyChanged(); }
        }
    }

    /// <summary>One (filter, rotation) combination captured tonight — the unit the Flat Handling
    /// box iterates over so flats are only taken for what was actually shot.</summary>
    public class FlatSpec {
        /// <summary>The target (incl. panel suffix) the lights were shot under — the container's
        /// Target is set to this while the combo's flats run, so NINA's $$TARGETNAME$$ file-pattern
        /// token drops the flats into the same folder tree as the lights.</summary>
        public string TargetName { get; set; } = "";
        /// <summary>Cloud project the target belongs to — the key for the flats ledgers
        /// (Auto Flats Per Project decides per project, not per mosaic panel).</summary>
        public string ProjectName { get; set; } = "";
        public string FilterName { get; set; } = "";
        /// <summary>Sky position angle the lights were taken at.</summary>
        public double RotationDeg { get; set; }
        /// <summary>Rotator mechanical position at capture time — what MoveMechanical must
        /// reproduce for flats, since sky angles are meaningless once the scope is parked.</summary>
        public float? MechanicalRotation { get; set; }
        // Full exposure spec of the lights — NINA's trained-flat table is keyed by
        // filter + gain + binning, so these are part of the combo identity and get
        // pushed into any Trained Flat Exposure instruction before each pass.
        public int Gain { get; set; }
        public int Offset { get; set; }
        public int BinX { get; set; } = 1;
        public int BinY { get; set; } = 1;
    }

    /// <summary>Persists tonight's captured (filter, rotation) combos so a NINA restart
    /// mid-night doesn't lose them before flats run at session end.</summary>
    internal class FlatSpecStore {
        public string NightDate { get; set; } = "";
        public List<FlatSpec> Specs { get; set; } = new List<FlatSpec>();
        public DateTime? FlatsCompletedUtc { get; set; }
        /// <summary>Combos whose flats pass was missed (safety hold / stop past the dawn window)
        /// under Auto Flats Per Project — made up at the next session's flats pass.</summary>
        public List<FlatSpec> CarryOver { get; set; } = new List<FlatSpec>();
        /// <summary>Night the carry-over combos were captured on; they expire after a few nights.</summary>
        public string CarryOverNight { get; set; } = "";
        /// <summary>Per-project flats ledgers from the cloud, snapshotted when each combo was
        /// recorded, so a mid-night NINA restart still has them for the dawn decision.</summary>
        public Dictionary<string, FlatsLedgerData> CloudLedgers { get; set; } = new Dictionary<string, FlatsLedgerData>(StringComparer.OrdinalIgnoreCase);

        private static string FilePath => System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NINA", "Plugins", "AstroPM.NINA.Plugin", "flat_specs.json");

        public static FlatSpecStore Load() {
            try {
                if (System.IO.File.Exists(FilePath))
                    return JsonConvert.DeserializeObject<FlatSpecStore>(System.IO.File.ReadAllText(FilePath)) ?? new FlatSpecStore();
            } catch { }
            return new FlatSpecStore();
        }

        public void Save() {
            try {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath));
                System.IO.File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
            } catch { }
        }
    }

    [ExportMetadata("Name", "Astro PM Instructions")]
    [ExportMetadata("Description", "Executes the Astro PM nightly imaging schedule — slew, filter, expose, dither per the simulation plan")]
    [ExportMetadata("Icon", "SequentialSVG")]
    [ExportMetadata("Category", "Astro PM Tools")]
    [Export(typeof(ISequenceItem))]
    [Export(typeof(ISequenceContainer))]
    [JsonObject(MemberSerialization.OptIn)]
    public class TargetInstructionSet : SequenceContainer, IDeepSkyObjectContainer {

        public static TargetInstructionSet ActiveInstance { get; private set; }

        private readonly IProfileService _profileService;
        private readonly ITelescopeMediator _telescopeMediator;
        private readonly IFilterWheelMediator _filterWheelMediator;
        private readonly IImagingMediator _imagingMediator;
        private readonly ICameraMediator _cameraMediator;
        private readonly IImageSaveMediator _imageSaveMediator;
        private readonly IImageHistoryVM _imageHistoryVM;
        private readonly IGuiderMediator _guiderMediator;
        private readonly IRotatorMediator _rotatorMediator;
        private readonly IDomeMediator _domeMediator;
        private readonly IDomeFollower _domeFollower;
        private readonly IPlateSolverFactory _plateSolverFactory;
        private readonly IWindowServiceFactory _windowServiceFactory;
        private readonly IFlatDeviceMediator _flatDeviceMediator;

        private readonly INighttimeCalculator _nighttimeCalculator;

        private InputTarget _target;
        public InputTarget Target {
            get => _target;
            set { _target = value; RaisePropertyChanged(); }
        }

        private NighttimeData _nighttimeData;
        public NighttimeData NighttimeData {
            get => _nighttimeData;
            private set { _nighttimeData = value; RaisePropertyChanged(); }
        }

        private List<SimLogEntry> _lastLog;
        private List<TimeSlot> _lastSlots;
        private List<TargetProfile> _lastProfiles;
        private List<TargetBlock> _blocks;
        private bool _hasChartData;
        private bool _scheduleBuilt;
        private DateTime _sessionEndUtc;

        /// <summary>Noon-to-noon night key the current schedule was built for (BuildSchedule's
        /// date). Guards IsStaleSession: a finished session only goes stale once rebuilding
        /// would key to a DIFFERENT night — otherwise the stale reset rebuilds the same
        /// already-over night and the loop spins reset→fetch→rebuild at full speed
        /// (observed Dome A 7/30/26: 33 cloud fetches in 30s after flats ran late).</summary>
        private DateTime _scheduleNightDate = DateTime.MinValue;

        /// <summary>When the last schedule build produced zero blocks. Guards the rebuild:
        /// without it, an empty build + a generic NINA loop container (Loop While Safe etc.)
        /// re-enters Execute instantly and re-fetches the cloud at HTTP speed — observed in
        /// the wild at ~4 calls/sec, sustained (API log, license 183, 7/29/26).</summary>
        // Stamped at the START of every schedule-build attempt (not on success) so no exit
        // path — empty result, exception mid-build, or cancellation — can leave the rebuild
        // cooldown unarmed. Field case 8/6/26: an exception thrown inside BuildSchedule left
        // the old empty-build stamp unset, and a generic NINA loop container re-entered
        // Execute at fetch speed for hours (license 133/201 runaway loops in the API log).
        private DateTime _lastBuildAttemptUtc = DateTime.MinValue;
        private static readonly TimeSpan EmptyRebuildCooldown = TimeSpan.FromMinutes(5);

        // Persisted across interruptions
        private int _currentBlockIndex;

        // ── Flat Handling ──
        // (filter, rotation) combos captured tonight; the Flat Handling box iterates these
        // at session complete. Guarded by lock(_flatSpecs) — captures record from the
        // execution loop while the UI reads the summary.
        private readonly List<FlatSpec> _flatSpecs = new List<FlatSpec>();
        private string _nightDate = "";
        private bool _flatsDone;
        // Auto Flats Per Project: combos carried over from a night whose flats pass was missed,
        // and the cloud ledgers (per project) captured alongside tonight's combos.
        private readonly List<FlatSpec> _carryOverSpecs = new List<FlatSpec>();
        private string _carryOverNight = "";
        private readonly Dictionary<string, FlatsLedgerData> _cloudLedgers = new Dictionary<string, FlatsLedgerData>(StringComparer.OrdinalIgnoreCase);
        private const int CarryOverMaxNights = 3;

        // Test mode: skip wait + viability check for current block
        private volatile bool _skipWait;
        // Skip current block and move to next
        private volatile bool _skipBlock;

        [ImportingConstructor]
        public TargetInstructionSet(
            IProfileService profileService,
            ITelescopeMediator telescopeMediator,
            IFilterWheelMediator filterWheelMediator,
            IImagingMediator imagingMediator,
            ICameraMediator cameraMediator,
            IImageSaveMediator imageSaveMediator,
            IImageHistoryVM imageHistoryVM,
            IGuiderMediator guiderMediator,
            IRotatorMediator rotatorMediator,
            IDomeMediator domeMediator,
            IDomeFollower domeFollower,
            IPlateSolverFactory plateSolverFactory,
            IWindowServiceFactory windowServiceFactory,
            INighttimeCalculator nighttimeCalculator,
            IFlatDeviceMediator flatDeviceMediator) : base(new SequentialStrategy()) {
            _profileService = profileService;
            _telescopeMediator = telescopeMediator;
            _filterWheelMediator = filterWheelMediator;
            _imagingMediator = imagingMediator;
            _cameraMediator = cameraMediator;
            _imageSaveMediator = imageSaveMediator;
            _imageHistoryVM = imageHistoryVM;
            _guiderMediator = guiderMediator;
            _rotatorMediator = rotatorMediator;
            _domeMediator = domeMediator;
            _domeFollower = domeFollower;
            _plateSolverFactory = plateSolverFactory;
            _windowServiceFactory = windowServiceFactory;
            _nighttimeCalculator = nighttimeCalculator;
            _flatDeviceMediator = flatDeviceMediator;

            NighttimeData = nighttimeCalculator.Calculate();

            // Cloud-applied settings (Options refresh / schedule build) flip FlatsEnabled in
            // settings.json — re-read it so the sequencer header checkbox tracks the change.
            AstroPMSettings.ExternallyChanged += () => {
                RaisePropertyChanged(nameof(FlatsEnabled));
                RaisePropertyChanged(nameof(FlatsFullSet));
                RaisePropertyChanged(nameof(FlatsStatusText));
                // Flat Handling or Auto Flats switched off (app, Options refresh, Simulator
                // panel): the make-up combos are no longer wanted — drop them now.
                DropCarryOverIfDisabled("setting changed");
            };

            // Initialize Target so other plugins (e.g. SequencerPlus) don't get a null
            // when they walk the tree looking for IDeepSkyObjectContainer before we execute.
            var astro = profileService.ActiveProfile.AstrometrySettings;
            Target = new InputTarget(
                Angle.ByDegree(astro.Latitude),
                Angle.ByDegree(astro.Longitude),
                astro.Horizon);

            // Add a placeholder so NINA never sees an empty container (which it would skip).
            // Our Execute() override handles all real work — this just prevents the skip.
            Add(new AstroPMPlaceholderItem());

            FlatsRunner = new SequentialContainer();
            FlatsRunner.AttachNewParent(this);
            FlatsSetupRunner = new SequentialContainer();
            FlatsSetupRunner.AttachNewParent(this);
            FlatsTeardownRunner = new SequentialContainer();
            FlatsTeardownRunner.AttachNewParent(this);
        }

        // ── Flat Handling: user-droppable instruction box ──
        // Runs once per (filter, rotation) combination captured tonight, at session complete.
        // Same serialization pattern as NINA's SequenceTrigger.TriggerRunner.

        /// <summary>Proxies the plugin-wide setting (settings.json) rather than serializing with the
        /// sequence: the desktop app's "Enable Flats Sequence" simulator checkbox pushes this value
        /// through the imaging-system cloud sync, and the plugin's own Simulator panel mirrors it —
        /// this checkbox in the sequencer is a third view of the same switch.</summary>
        public bool FlatsEnabled {
            get => AstroPMSettings.Load().FlatsEnabled;
            set {
                var s = AstroPMSettings.Load();
                if (s.FlatsEnabled != value) {
                    s.FlatsEnabled = value;
                    s.Save();
                    AstroPMSettings.NotifyExternallyChanged(); // refresh Simulator panel mirror
                }
                RaisePropertyChanged();
            }
        }

        /// <summary>Same plugin-wide proxy pattern as <see cref="FlatsEnabled"/>: with flats on,
        /// run the Flat Handling instructions for EVERY filter in the wheel at each
        /// target/rotation captured tonight — not just the filters actually shot.</summary>
        public bool FlatsFullSet {
            get => AstroPMSettings.Load().FlatsFullSet;
            set {
                var s = AstroPMSettings.Load();
                if (s.FlatsFullSet != value) {
                    s.FlatsFullSet = value;
                    s.Save();
                    AstroPMSettings.NotifyExternallyChanged(); // refresh Simulator panel mirror
                }
                RaisePropertyChanged();
            }
        }

        /// <summary>Read-only status pill for the sequencer header — the ON/OFF sliders that
        /// used to sit there wrote the global setting but read as a local instruction-set
        /// toggle, which misled users. The real switches live in the desktop app / plugin
        /// Simulator panel.</summary>
        public string FlatsStatusText {
            get {
                if (!FlatsEnabled) return "Flats: Off  (enable in Astro PM app or Simulator panel)";
                var s = FlatsFullSet ? "Flats: Enabled · Full Filter Set" : "Flats: Enabled";
                if (FlatsAutoPerProject)
                    s += FlatsAutoMode == "TimeBased" ? $" · Auto per project every {FlatsAutoIntervalDays} d" : " · Auto once per project";
                return s;
            }
        }

        /// <summary>Auto Flats Per Project (plugin-wide setting, pushed from the desktop).</summary>
        public bool FlatsAutoPerProject => AstroPMSettings.Load().FlatsAutoPerProject;
        public string FlatsAutoMode => AstroPMSettings.Load().FlatsAutoMode == "TimeBased" ? "TimeBased" : "OncePerProject";
        public int FlatsAutoIntervalDays => Math.Max(1, AstroPMSettings.Load().FlatsAutoIntervalDays);

        /// <summary>Runs once, before the per-combo loop — park mount, close flat panel, light on.</summary>
        [JsonProperty]
        public SequentialContainer FlatsSetupRunner { get; protected set; }

        /// <summary>Runs once per (filter, rotation) combo with wheel + rotator pre-set.</summary>
        [JsonProperty]
        public SequentialContainer FlatsRunner { get; protected set; }

        /// <summary>Runs once, after all combos — light off, open panel, etc.</summary>
        [JsonProperty]
        public SequentialContainer FlatsTeardownRunner { get; protected set; }

        private string _flatsSummary = "No exposures captured yet tonight.";
        /// <summary>UI line under the Flat Handling header: tonight's captured rotation → filter combos.</summary>
        public string FlatsSummary {
            get => _flatsSummary;
            private set { _flatsSummary = value; RaisePropertyChanged(); }
        }

        // ── Serialization hygiene ──
        // The placeholder is runtime-only and not MEF-exported, so NINA cannot re-create
        // it when loading a saved sequence: each save/load cycle logs an "unknown sequence
        // item" error and accumulates an UnknownSequenceItem fossil in the JSON. Strip
        // runtime children before save, and scrub fossils left by older builds after load.

        // Same for the flats shim: during a flats pass the runners are parented to the
        // runtime-only FlatsIsolationContainer, and NINA serializes each container's Parent.
        // A save mid-pass (10/3 Dome A, 05:54 during Before Flats) wrote the shim into the
        // startup sequence, so every load logged "unknown sequence container". Re-home the
        // runners to us for the length of the save, then hand them back to the shim.
        private List<SequentialContainer> _runnersOnShimDuringSave;

        [OnSerializing]
        private void OnSerializingStripRuntimeItems(StreamingContext context) {
            ScrubPlaceholders();
            var shim = _flatsShim;
            if (shim == null) return;
            _runnersOnShimDuringSave = new List<SequentialContainer>();
            foreach (var runner in new[] { FlatsSetupRunner, FlatsRunner, FlatsTeardownRunner }) {
                if (runner != null && ReferenceEquals(runner.Parent, shim)) {
                    runner.AttachNewParent(this);
                    _runnersOnShimDuringSave.Add(runner);
                }
            }
        }

        [OnSerialized]
        private void OnSerializedRestorePlaceholder(StreamingContext context) {
            EnsurePlaceholder();
            var moved = _runnersOnShimDuringSave;
            _runnersOnShimDuringSave = null;
            var shim = _flatsShim;
            if (moved == null || shim == null) return;   // pass ended mid-save: stay home
            foreach (var runner in moved) runner.AttachNewParent(shim);
        }

        [OnDeserialized]
        private void OnDeserializedScrubFossils(StreamingContext context) {
            ScrubPlaceholders();
            EnsurePlaceholder();
            // Older saved sequences predate the Flat Handling box — the property is absent
            // from their JSON, so the deserializer leaves the ctor-created (or null) runner.
            if (FlatsRunner == null) FlatsRunner = new SequentialContainer();
            FlatsRunner.AttachNewParent(this);
            if (FlatsSetupRunner == null) FlatsSetupRunner = new SequentialContainer();
            FlatsSetupRunner.AttachNewParent(this);
            if (FlatsTeardownRunner == null) FlatsTeardownRunner = new SequentialContainer();
            FlatsTeardownRunner.AttachNewParent(this);
        }

        private void ScrubPlaceholders() {
            foreach (var item in GetItemsSnapshot()) {
                if (item is AstroPMPlaceholderItem || item is UnknownSequenceItem) {
                    Items.Remove(item);
                }
            }
        }

        private void EnsurePlaceholder() {
            if (Items.Count == 0) Add(new AstroPMPlaceholderItem());
        }

        private TargetInstructionSet(TargetInstructionSet cloneMe) : this(
            cloneMe._profileService, cloneMe._telescopeMediator, cloneMe._filterWheelMediator,
            cloneMe._imagingMediator, cloneMe._cameraMediator, cloneMe._imageSaveMediator, cloneMe._imageHistoryVM,
            cloneMe._guiderMediator, cloneMe._rotatorMediator,
            cloneMe._domeMediator, cloneMe._domeFollower,
            cloneMe._plateSolverFactory, cloneMe._windowServiceFactory,
            cloneMe._nighttimeCalculator, cloneMe._flatDeviceMediator) {
            CopyMetaData(cloneMe);
            FlatsRunner = (SequentialContainer)cloneMe.FlatsRunner.Clone();
            FlatsRunner.AttachNewParent(this);
            FlatsSetupRunner = (SequentialContainer)cloneMe.FlatsSetupRunner.Clone();
            FlatsSetupRunner.AttachNewParent(this);
            FlatsTeardownRunner = (SequentialContainer)cloneMe.FlatsTeardownRunner.Clone();
            FlatsTeardownRunner.AttachNewParent(this);
        }

        public List<SimLogEntry> LastLog => _lastLog;
        public List<TimeSlot> LastSlots => _lastSlots;
        public List<TargetProfile> LastProfiles => _lastProfiles;
        public DateTime SessionEndUtc => _sessionEndUtc;
        public bool ScheduleBuilt => _scheduleBuilt;

        private ObservableCollection<SimLogEntry> _logEntries;
        private int _currentLogIndex = -1;
        private SimLogEntry _activeLogEntry;

        public ObservableCollection<SimLogEntry> LogEntries {
            get => _logEntries;
            private set { _logEntries = value; RaisePropertyChanged(); }
        }

        public SimLogEntry ActiveLogEntry {
            get => _activeLogEntry;
            private set { _activeLogEntry = value; RaisePropertyChanged(); }
        }

        private bool _autoScrollLog = true;
        public bool AutoScrollLog {
            get => _autoScrollLog;
            set { _autoScrollLog = value; RaisePropertyChanged(); }
        }

        public bool HasChartData {
            get => _hasChartData;
            private set { _hasChartData = value; RaisePropertyChanged(); }
        }

        private string _liveTarget = "Schedule will be built when the sequence starts...";
        private string _liveCommand = "Wait";
        private string _liveFilter = "";
        private string _liveExposure = "";
        private string _liveSub = "";
        private string _livePanel = "";
        private string _liveRA = "";
        private string _liveDEC = "";
        private string _liveRotation = "";
        private string _liveGainOffset = "";
        private bool _hasLiveStatus = true;

        private ObservableCollection<BlockSummary> _blockSummaries;
        private bool _hasBlockInfo;

        public ObservableCollection<BlockSummary> BlockSummaries {
            get => _blockSummaries;
            private set { _blockSummaries = value; RaisePropertyChanged(); }
        }
        public bool HasBlockInfo { get => _hasBlockInfo; private set { _hasBlockInfo = value; RaisePropertyChanged(); } }

        public string LiveTarget { get => _liveTarget; private set { _liveTarget = value; RaisePropertyChanged(); } }
        public string LiveCommand { get => _liveCommand; private set { _liveCommand = value; RaisePropertyChanged(); } }
        public string LiveFilter { get => _liveFilter; private set { _liveFilter = value; RaisePropertyChanged(); } }
        public string LiveExposure { get => _liveExposure; private set { _liveExposure = value; RaisePropertyChanged(); } }
        public string LiveSub { get => _liveSub; private set { _liveSub = value; RaisePropertyChanged(); } }
        public string LivePanel { get => _livePanel; private set { _livePanel = value; RaisePropertyChanged(); } }
        public string LiveRA { get => _liveRA; private set { _liveRA = value; RaisePropertyChanged(); } }
        public string LiveDEC { get => _liveDEC; private set { _liveDEC = value; RaisePropertyChanged(); } }
        public string LiveRotation { get => _liveRotation; private set { _liveRotation = value; RaisePropertyChanged(); } }
        public string LiveGainOffset { get => _liveGainOffset; private set { _liveGainOffset = value; RaisePropertyChanged(); } }
        public bool HasLiveStatus { get => _hasLiveStatus; private set { _hasLiveStatus = value; RaisePropertyChanged(); } }

        private string _cloudFetchStatus = "";
        /// <summary>One-line target-fetch summary shown above the block list: count + source/time, or cache age / offline mode.</summary>
        public string CloudFetchStatus { get => _cloudFetchStatus; private set { _cloudFetchStatus = value; RaisePropertyChanged(); } }

        public System.Windows.Input.ICommand ResetScheduleCommand => new RelayCommand(async _ => {
            _scheduleBuilt = false;
            _blocks = null;
            _currentBlockIndex = 0;
            _sessionEndUtc = DateTime.MinValue;
            _scheduleNightDate = DateTime.MinValue;
            _lastBuildAttemptUtc = DateTime.MinValue;   // manual reset always allows an immediate fetch
            _lastLog = null;
            _lastSlots = null;
            _lastProfiles = null;
            HasChartData = false;
            LogEntries = null;
            BlockSummaries = null;
            HasBlockInfo = false;
            ResetLiveStatus();
            // Manual reset means a truly clean slate — flat tracking included. Clear the
            // in-memory night state AND the on-disk recovery store, or the next build's
            // SyncFlatTrackingForNight would just recover tonight's combos/done-flag back.
            lock (_flatSpecs) _flatSpecs.Clear();
            _flatsDone = false;
            _nightDate = "";
            lock (_carryOverSpecs) _carryOverSpecs.Clear();
            _carryOverNight = "";
            lock (_cloudLedgers) _cloudLedgers.Clear();
            new FlatSpecStore().Save();
            UpdateFlatsSummary();
            // Also clear the checkmarks/progress the last flats pass left on the drop-zone
            // instructions — including nested loop counters (see ResetRunnerProgress).
            ResetRunnerProgress(FlatsSetupRunner);
            ResetRunnerProgress(FlatsRunner);
            ResetRunnerProgress(FlatsTeardownRunner);
            // The container itself still carries NINA-side FINISHED status from the previous
            // run — without resetting it, a stopped-and-restarted sequence SKIPS Execute()
            // entirely and the schedule is never re-fetched (field report: users had to also
            // right-click → Reset Progress on the instruction set before targets came back).
            // One button now does both. Skipped while RUNNING: the live pass already rebuilds
            // via _scheduleBuilt = false, and yanking statuses mid-execution confuses NINA.
            if (Status != global::NINA.Core.Enum.SequenceEntityStatus.RUNNING)
                ResetRunnerProgress(this);
            global::NINA.Core.Utility.Logger.Info("AstroPM | Manual reset — schedule, flat tracking, and container progress cleared, will re-fetch on next run");
            Notification.ShowInformation("Astro PM: Schedule reset. Start the sequence to fetch new targets.");
        });

        public System.Windows.Input.ICommand SkipBlockCommand => new RelayCommand(async _ => {
            _skipBlock = true;
            _skipWait = true; // also break out of wait if waiting
            global::NINA.Core.Utility.Logger.Info("AstroPM | Skip Block pressed — advancing to next block");
        });

        public System.Windows.Input.ICommand LoadToFramingCommand => new RelayCommand(async param => {
            if (!(param is BlockSummary block)) return;
            try {
                var app = System.Windows.Application.Current;
                if (app?.MainWindow?.DataContext == null) return;
                var mainDC = app.MainWindow.DataContext;
                var framingVM = mainDC.GetType().GetProperty("FramingAssistantVM")?.GetValue(mainDC);
                if (framingVM == null) return;

                // Set coordinates
                FramingInjector.SetCoordinatesDirect(framingVM, block.RaHours, block.DecDegrees);

                // Set target name
                var searchVM = framingVM.GetType().GetProperty("DeepSkyObjectSearchVM")?.GetValue(framingVM);
                if (searchVM != null) {
                    var setName = searchVM.GetType().GetMethod("SetTargetNameWithoutSearch");
                    if (setName != null) setName.Invoke(searchVM, new object[] { block.TargetName });
                    else searchVM.GetType().GetProperty("TargetName")?.SetValue(searchVM, block.TargetName);
                }

                // Navigate to framing tab
                foreach (var prop in mainDC.GetType().GetProperties()) {
                    var changeTab = prop.PropertyType.GetMethod("ChangeTab");
                    if (changeTab != null) {
                        var mediator = prop.GetValue(mainDC);
                        if (mediator != null) {
                            var paramType = changeTab.GetParameters()[0].ParameterType;
                            var framingValue = Enum.Parse(paramType, "FRAMINGASSISTANT");
                            changeTab.Invoke(mediator, new[] { framingValue });
                            break;
                        }
                    }
                }

                global::NINA.Core.Utility.Logger.Info($"AstroPM | Loaded {block.TargetName} to Framing Assistant");
            } catch (Exception ex) {
                global::NINA.Core.Utility.Logger.Warning($"AstroPM | Failed to load to Framing: {ex.Message}");
            }
        });

        private void BuildBlockSummaries() {
            var tz = TimeZoneInfo.Local;
            double lonDeg = _profileService.ActiveProfile.AstrometrySettings.Longitude;
            var summaries = new ObservableCollection<BlockSummary>();
            for (int i = 0; i < _blocks.Count; i++) {
                var b = _blocks[i];
                var start = TimeZoneInfo.ConvertTimeFromUtc(b.UtcStart, tz).ToString("h:mm tt");
                var end = TimeZoneInfo.ConvertTimeFromUtc(b.UtcEnd, tz).ToString("h:mm tt");
                var coords = $"RA {b.RaHours:F4}h  DEC {b.DecDegrees:F3}°  Rot: {b.RotationDeg:F1}°";
                var meridian = ComputeMeridianTransit(b.RaHours, lonDeg, b.UtcStart, b.UtcEnd, tz);
                summaries.Add(new BlockSummary {
                    Number = i + 1,
                    TargetName = b.TargetName,
                    TimeRange = $"{start} — {end}",
                    PlannedCount = b.Entries.Count(e => e.Command == "Image" || e.Command == "Bonus"),
                    Coords = coords,
                    RaHours = b.RaHours,
                    DecDegrees = b.DecDegrees,
                    RotationDeg = b.RotationDeg,
                    MeridianInfo = meridian,
                    IsCurrent = i == _currentBlockIndex,
                });
            }
            BlockSummaries = summaries;
            HasBlockInfo = summaries.Count > 0;
        }

        /// <summary>
        /// Returns "Meridian: HH:MM" local time if the target transits during the block, otherwise empty.
        /// Transit occurs when Local Sidereal Time equals the target's RA.
        /// </summary>
        private static string ComputeMeridianTransit(double raHours, double longitudeDeg, DateTime utcStart, DateTime utcEnd, TimeZoneInfo tz) {
            // J2000 epoch: 2000-01-01 12:00 UT
            // GMST at J2000.0 = 18.697374558 hours
            // Earth sidereal rate = 1.00273790935 sidereal hours per solar hour
            const double gmstJ2000 = 18.697374558;
            const double siderealRate = 1.00273790935;
            double j2000Epoch = 2451545.0; // JD of J2000

            // Julian date of block start
            double jdStart = ToJulianDate(utcStart);

            // GMST at block start (hours)
            double daysSinceJ2000 = jdStart - j2000Epoch;
            double gmstStart = gmstJ2000 + siderealRate * 24.0 * daysSinceJ2000;

            // LST at block start
            double lstStart = gmstStart + longitudeDeg / 15.0;
            lstStart = ((lstStart % 24.0) + 24.0) % 24.0;

            // Hour angle difference: how many sidereal hours until RA crosses meridian
            double haToTransit = raHours - lstStart;
            if (haToTransit < 0) haToTransit += 24.0;

            // Convert sidereal hours to solar hours
            double solarHoursToTransit = haToTransit / siderealRate;

            DateTime transitUtc = utcStart.AddHours(solarHoursToTransit);

            // Check if transit falls within this block
            if (transitUtc >= utcStart && transitUtc <= utcEnd) {
                var transitLocal = TimeZoneInfo.ConvertTimeFromUtc(transitUtc, tz);
                return $"Meridian: {transitLocal:h:mm tt}";
            }
            return "";
        }

        private static double ToJulianDate(DateTime utc) {
            int y = utc.Year, m = utc.Month, d = utc.Day;
            if (m <= 2) { y--; m += 12; }
            int A = y / 100;
            int B = 2 - A + A / 4;
            double jd = Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * (m + 1)) + d + B - 1524.5;
            jd += (utc.Hour + utc.Minute / 60.0 + utc.Second / 3600.0) / 24.0;
            return jd;
        }

        private void UpdateCurrentBlock() {
            if (_blockSummaries == null) return;
            foreach (var s in _blockSummaries)
                s.IsCurrent = s.Number == _currentBlockIndex + 1;
        }

        private static string FriendlyCommand(string cmd) => cmd switch {
            "Wait" => "Waiting",
            "Slew" => "Slew, Center, & Rotate",
            "Guide" => "Start Guiding",
            "Filter" => "Filter Change",
            "Triggers" => "Running Triggers",
            "Dither" => "Dithering",
            "Image" => "Imaging",
            "Complete" => "Complete",
            _ => cmd,
        };

        private void UpdateLiveStatus(string command, TargetBlock block, string filter = null,
            string exposure = null, string sub = null, string panel = null, string gainOffset = null) {
            LiveCommand = FriendlyCommand(command);
            LiveTarget = block?.TargetName ?? "";
            LiveRA = block != null ? $"{block.RaHours:F4}h" : "";
            LiveDEC = block != null ? $"{block.DecDegrees:F3}°" : "";
            LiveRotation = block != null ? $"{block.RotationDeg:F1}°" : "";
            LiveFilter = filter ?? "";
            LiveExposure = !string.IsNullOrEmpty(sub) && !string.IsNullOrEmpty(exposure)
                ? $"#{sub} · {exposure}" : "";
            LiveSub = sub ?? "";
            LivePanel = panel ?? "";
            LiveGainOffset = gainOffset ?? "";
            HasLiveStatus = true;
            AdvanceActiveLogEntry();
        }

        private void AdvanceActiveLogEntry() {
            if (_lastLog == null || _lastLog.Count == 0) return;
            var now = DateTime.UtcNow;
            SimLogEntry best = null;
            for (int i = 0; i < _lastLog.Count; i++) {
                if (_lastLog[i].UtcTime == default) continue;
                if (_lastLog[i].UtcTime <= now) best = _lastLog[i];
                else break;
            }
            if (best != null && best != _activeLogEntry)
                ActiveLogEntry = best;
        }

        public bool HasBlocksRemaining {
            get {
                if (!_scheduleBuilt) return true;
                // A stale schedule from a previous night must keep the loop condition
                // TRUE: NINA evaluates conditions BEFORE running a container's items,
                // so returning false here skips the instruction set entirely and the
                // stale-session reset in Execute() never gets the chance to rebuild.
                if (IsStaleSession) return true;
                // Never flip false while a block is actively executing — the end-of-night
                // grace sub deliberately runs past _sessionEndUtc, and the condition
                // watchdog would cancel it mid-integration (see ExecuteNextBlock).
                if (_executingBlock) return true;
                if (!BlocksExhausted) return true;
                // Hold the container alive until the Flat Handling pass finishes. The parent
                // loop conditions key off this property, and NINA's condition watchdog CANCELS
                // the running instruction the moment it flips false — which without this guard
                // is the exact moment the flats need to run (field-tested 7/10: the daily loop
                // reset + watchdog cancel killed the flats pass at session end).
                return FlatsPending;
            }
        }

        /// <summary>True when every scheduled block (and the session window) is over — the
        /// pre-flats notion of "night finished" that triggers the Flat Handling pass.</summary>
        private bool BlocksExhausted {
            get {
                if (_blocks == null || _blocks.Count == 0) return true;
                var now = DateTime.UtcNow;
                if (now >= _sessionEndUtc) return true;
                for (int i = _currentBlockIndex; i < _blocks.Count; i++) {
                    if (_blocks[i].UtcEnd > now) return false;
                }
                return true;
            }
        }

        /// <summary>True when the Flat Handling pass still has work to do tonight: enabled, has
        /// instructions, has recorded combos, and hasn't completed yet.</summary>
        private bool FlatsPending {
            get {
                if (_flatsDone || !FlatsEnabled) return false;
                bool hasInstructions = (FlatsSetupRunner?.GetItemsSnapshot().Count > 0)
                    || (FlatsRunner?.GetItemsSnapshot().Count > 0)
                    || (FlatsTeardownRunner?.GetItemsSnapshot().Count > 0);
                if (!hasInstructions) return false;
                lock (_flatSpecs) if (_flatSpecs.Count > 0) return true;
                // Carried-over combos (Auto Flats Per Project) pend only while the CURRENT
                // session is live — never for a stale schedule, or they'd hold last night's
                // session open at dusk and run flats after startup (the 9/18 incident).
                lock (_carryOverSpecs) {
                    return _carryOverSpecs.Count > 0 && _scheduleBuilt && _sessionEndUtc != DateTime.MinValue
                        && DateTime.UtcNow < _sessionEndUtc.AddHours(StaleAfterHours);
                }
            }
        }

        /// <summary>The dawn flats window has passed with the pass still pending (sequence
        /// stopped mid-flats or never reached them, then restarted hours or days later).
        /// Drop the recorded combos and mark the pass done so neither Execute's stale reset
        /// nor a same-night NINA restart (which reloads the store) resumes them.</summary>
        private void MarkFlatsMissed() {
            int count;
            List<FlatSpec> missed;
            lock (_flatSpecs) {
                missed = _flatSpecs.ToList();
                count = missed.Count;
                _flatSpecs.Clear();
            }
            _flatsDone = true;
            var age = DateTime.UtcNow - _sessionEndUtc;
            if (FlatsAutoPerProject && count > 0) {
                // Auto Flats Per Project: the combos still need flats — make them up at the
                // next session's pass instead of dropping them (safety-hold night, or the
                // sequence stopped before dawn).
                lock (_carryOverSpecs) {
                    foreach (var m in missed)
                        if (!_carryOverSpecs.Any(c => SameCombo(c, m))) _carryOverSpecs.Add(m);
                    _carryOverNight = string.IsNullOrEmpty(_carryOverNight) ? _nightDate : _carryOverNight;
                }
                SaveFlatStore(new List<FlatSpec>(), DateTime.UtcNow);
                UpdateFlatsSummary();
                global::NINA.Core.Utility.Logger.Warning(
                    $"AstroPM | Flats: window missed — session ended {_sessionEndUtc:MMM d HH:mm} UTC ({age.TotalHours:F1} h ago); carrying {count} filter/rotation combos over to the next session's flats pass (Auto Flats Per Project)");
                Notification.ShowWarning($"Astro PM: Flats window missed ({age.TotalHours:F0} h since session end) — {count} combos will be made up after the next session.");
                return;
            }
            SaveFlatStore(new List<FlatSpec>(), DateTime.UtcNow);
            UpdateFlatsSummary();
            global::NINA.Core.Utility.Logger.Warning(
                $"AstroPM | Flats: window missed — session ended {_sessionEndUtc:MMM d HH:mm} UTC ({age.TotalHours:F1} h ago); discarding {count} pending filter/rotation combos, flats will not be re-run");
            Notification.ShowWarning($"Astro PM: Flats window missed ({age.TotalHours:F0} h since session end) — {count} pending combos discarded.");
        }

        /// <summary>Carried-over combos exist only to be made up under Auto Flats Per Project.
        /// If Flat Handling or Auto Flats has been switched off since they were parked, the
        /// user has said they don't want them — discard them (logged) rather than let them
        /// sit for up to three nights and spring back when the switch comes on again.</summary>
        private void DropCarryOverIfDisabled(string reason) {
            if (FlatsEnabled && FlatsAutoPerProject) return;
            int n;
            lock (_carryOverSpecs) { n = _carryOverSpecs.Count; _carryOverSpecs.Clear(); }
            if (n == 0) return;
            _carryOverNight = "";
            List<FlatSpec> specs;
            lock (_flatSpecs) specs = _flatSpecs.ToList();
            SaveFlatStore(specs, _flatsDone ? DateTime.UtcNow : (DateTime?)null);
            global::NINA.Core.Utility.Logger.Info(
                $"AstroPM | Flats: dropped {n} carried-over combos — {(FlatsEnabled ? "Auto Flats Per Project" : "Flat Handling")} is off ({reason})");
        }

        private static bool SameCombo(FlatSpec a, FlatSpec b) =>
            string.Equals(a.TargetName, b.TargetName, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.FilterName, b.FilterName, StringComparison.OrdinalIgnoreCase)
            && Math.Abs(a.RotationDeg - b.RotationDeg) < 0.05
            && a.Gain == b.Gain && a.Offset == b.Offset && a.BinX == b.BinX && a.BinY == b.BinY;

        /// <summary>Single writer for flat_specs.json: tonight's combos + completion stamp, plus
        /// the Auto Flats carry-over and cloud-ledger snapshots that must survive a restart.</summary>
        private void SaveFlatStore(List<FlatSpec> specs, DateTime? completedUtc) {
            List<FlatSpec> carry; Dictionary<string, FlatsLedgerData> ledgers;
            lock (_carryOverSpecs) carry = _carryOverSpecs.ToList();
            lock (_cloudLedgers) ledgers = new Dictionary<string, FlatsLedgerData>(_cloudLedgers, StringComparer.OrdinalIgnoreCase);
            new FlatSpecStore {
                NightDate = _nightDate, Specs = specs, FlatsCompletedUtc = completedUtc,
                CarryOver = carry, CarryOverNight = carry.Count > 0 ? _carryOverNight : "", CloudLedgers = ledgers,
            }.Save();
        }

        /// <summary>True when the built schedule is left over from a finished night. A
        /// session goes stale StaleAfterHours after its end time: long enough that the
        /// dawn wind-down (watchdog condition checks, flats, the daily loop's dawn tasks)
        /// still sees it as tonight's and the nightly loop exits cleanly. Additionally the
        /// current night key must differ from the one the schedule was built for — before
        /// local noon a rebuild would reproduce the SAME finished night (BuildSchedule's
        /// noon boundary), so resetting then just spins reset→fetch→rebuild; the finished
        /// night reads as complete instead and the loop exits. After noon the key flips,
        /// the session goes stale, and the reset rebuilds for the coming night.</summary>
        public bool IsStaleSession {
            get {
                if (!_scheduleBuilt || _sessionEndUtc == DateTime.MinValue) return false;
                if (DateTime.UtcNow < _sessionEndUtc.AddHours(StaleAfterHours)) return false;
                return CurrentNightKey() != _scheduleNightDate;
            }
        }

        private const double StaleAfterHours = 2;

        /// <summary>The noon-to-noon night identifier for "now": before local noon we are
        /// still in the night that started yesterday evening. Must match BuildSchedule's
        /// date choice exactly — IsStaleSession compares against it.</summary>
        private static DateTime CurrentNightKey() {
            var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, TimeZoneInfo.Local);
            return localNow.Hour < 12 ? DateTime.Today.AddDays(-1) : DateTime.Today;
        }

        public void ResetForNewNight() {
            _scheduleBuilt = false;
            _blocks = null;
            _currentBlockIndex = 0;
            _sessionEndUtc = DateTime.MinValue;
            _scheduleNightDate = DateTime.MinValue;
            _lastBuildAttemptUtc = DateTime.MinValue;   // a reset (manual or stale) always allows an immediate fetch
            Logger.Info("AstroPM | Schedule reset for new night");
        }

        public override async Task Execute(IProgress<ApplicationStatus> progress, CancellationToken token) {
            // NOTE: We intentionally do NOT call base.Execute() here.
            // base.Execute() would run the SequentialStrategy on the placeholder child,
            // which is not what we want. Instead we manage execution ourselves and
            // manually fire parent triggers between exposures.

            ActiveInstance = this;

            // Reset all live status from any previous run
            ResetLiveStatus();

            // The Flat Handling pass belongs to the dawn right after session end. If the
            // sequence is started again once that window has passed — later the same
            // morning, that evening, or days later — the flats were missed and are over:
            // discard them so the stale reset below can rebuild instead of resuming a
            // previous night's flats. (9/18/26: the 9/17 session's combos were still
            // pending at the next dusk; both domes ran Before Flats + Close Dome Shutter
            // right after the startup had opened the shutter, and the cancel left Dome B's
            // dome driver reporting "closing" until dawn.)
            if (FlatsPending && _sessionEndUtc != DateTime.MinValue
                && DateTime.UtcNow >= _sessionEndUtc.AddHours(StaleAfterHours)) {
                MarkFlatsMissed();
            }

            // A schedule left over from a finished night (per IsStaleSession) must be
            // discarded — the in-memory state survives sequence stop/restart, so
            // without this reset a re-run instantly reports "session complete"
            // instead of building tonight's schedule.
            // FlatsPending exception: right at session end the flats pass hasn't run yet
            // (and a stop/restart during flats re-enters here) — resetting would nuke the
            // blocks and rebuild mid-flats. Skip the reset until flats complete. Bounded
            // by the missed-window check above, so it can never hold a stale session
            // past StaleAfterHours.
            if (IsStaleSession && !FlatsPending) {
                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Stale session from previous night (ended {_sessionEndUtc:MMM d HH:mm} UTC) — resetting to rebuild");
                ResetForNewNight();
            }

            // Only rebuild the schedule if we don't have an active session.
            // After a block completes, the loop calls Execute() again — we must NOT
            // rebuild because DateTime.Today flips after midnight, which would
            // reschedule remaining blocks for tomorrow night instead of continuing tonight.
            // (A session still in progress has _sessionEndUtc in the future, so the
            // stale-session reset above never fires mid-night.)
            bool needsBuild = !_scheduleBuilt
                || _blocks == null || _blocks.Count == 0;

            // An empty OR failed build keeps needsBuild true forever — throttle retries so a
            // hot outer loop can't hammer the cloud. Gated on the last ATTEMPT, not on
            // _scheduleBuilt: an exception inside BuildSchedule leaves _scheduleBuilt false,
            // and the old gate then never throttled (the 1.4.10/1.5.3 runaway-loop hole).
            // Within the cooldown: no fetch, just a short cancellable wait so the wrapping
            // loop container isn't a busy-spin.
            if (needsBuild
                && DateTime.UtcNow - _lastBuildAttemptUtc < EmptyRebuildCooldown) {
                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Execute: last build attempt was {(DateTime.UtcNow - _lastBuildAttemptUtc).TotalSeconds:F0}s ago with no schedule to run — waiting out the {EmptyRebuildCooldown.TotalMinutes:F0}m rebuild cooldown (no cloud fetch)");
                await Task.Delay(TimeSpan.FromSeconds(30), token);
                return;
            }

            try {
                if (needsBuild) {
                    var reason = !_scheduleBuilt ? "first run or reset" : "no blocks";
                    global::NINA.Core.Utility.Logger.Info($"AstroPM | Execute: building schedule ({reason})");
                    _lastBuildAttemptUtc = DateTime.UtcNow;   // arm the cooldown BEFORE the attempt
                    await BuildSchedule(progress, token);
                    _scheduleBuilt = true;
                    if (_blocks == null || _blocks.Count == 0) {
                        // A rebuild after a mid-flats crash can legitimately yield zero blocks
                        // (the night is over) while recovered combos still await flats — run
                        // them now rather than stranding them behind the early return.
                        if (FlatsPending) await RunFlatsIfNeeded(progress, token);

                        // An empty build must still stamp a session end: HasBlocksRemaining
                        // reads this state as complete, and without a timestamp IsStaleSession
                        // could never flip it stale — every later sequence start would skip
                        // the instruction set until NINA restarts.
                        _sessionEndUtc = DateTime.UtcNow;
                        _lastBuildAttemptUtc = DateTime.UtcNow;   // re-stamp at completion so slow fetches get the full cooldown
                        global::NINA.Core.Utility.Logger.Info(
                            $"AstroPM | Schedule build produced no blocks — next rebuild attempt in {EmptyRebuildCooldown.TotalMinutes:F0}m");
                        return;
                    }
                } else {
                    global::NINA.Core.Utility.Logger.Info(
                        $"AstroPM | Execute: continuing active session — block {_currentBlockIndex + 1}/{_blocks.Count}, session ends {_sessionEndUtc:HH:mm} UTC");
                }

                await ExecuteNextBlock(progress, token);

                // After the block finishes, run flats + show "Session Complete" if no blocks
                // remain. Trigger off BlocksExhausted, NOT HasBlocksRemaining — the latter
                // deliberately stays true while flats are pending (see HasBlocksRemaining).
                if (BlocksExhausted) {
                    global::NINA.Core.Utility.Logger.Info("AstroPM | All blocks finished — session complete");

                    // Flat Handling: run the user's dropped-in flat instructions once per
                    // (filter, rotation) combination captured tonight. Runs here — inside the
                    // nightly loop at session complete — so multi-night setups get flats every
                    // night, not just when the outer sequence ends. The parent loop conditions
                    // stay true (FlatsPending) until this finishes, so the condition watchdog
                    // can't cancel us mid-flats.
                    await RunFlatsIfNeeded(progress, token);

                    LiveCommand = "Session Complete";
                    HasLiveStatus = true;
                }
            } catch (OperationCanceledException) {
                // Sequence was stopped — reset status displays. Also disarm the rebuild
                // cooldown: a user stopping and restarting the sequence expects an
                // immediate fresh fetch, not a 5-minute wait (the cooldown exists for
                // unattended runaway loops, and those don't cancel — they throw).
                _lastBuildAttemptUtc = DateTime.MinValue;
                ResetLiveStatus();
                progress?.Report(new ApplicationStatus { Status = "" });
                throw;
            }
        }

        private void ResetLiveStatus() {
            HasLiveStatus = false;
            LiveCommand = "";
            LiveTarget = "";
            LiveFilter = "";
            LiveExposure = "";
            LiveSub = "";
            LiveGainOffset = "";
            LivePanel = "";
            if (_blockSummaries != null) {
                foreach (var bs in _blockSummaries) {
                    if (bs.Status != "completed")
                        bs.Status = "";
                }
            }
        }

        private async Task BuildSchedule(IProgress<ApplicationStatus> progress, CancellationToken token) {
            var settings = AstroPMSettings.Load();
            if (string.IsNullOrEmpty(settings.SyncToken)) {
                Notification.ShowWarning("Astro PM: No sync token configured. Open plugin Options to connect.");
                return;
            }

            progress?.Report(new ApplicationStatus { Status = "Astro PM: Fetching targets..." });

            List<ProjectTarget> targets = null;
            string fetchSourceDesc = "";
            if (!settings.OfflineMode) {
                var apiService = new AstroPMApiService();
                // Pull the selected rig's latest settings (strategy/dither/etc. + site/telescope/camera)
                // before fetching/filtering so the schedule reflects current desktop-app changes.
                // Warnings (settings not applied / NINA-profile location mismatch) are logged inside.
                var astroLoc = _profileService.ActiveProfile.AstrometrySettings;
                await ImagingSystemSettingsService.ApplySelectedFromCloudAsync(
                    settings, apiService, astroLoc.Latitude, astroLoc.Longitude, token);
                // Advisory camera-modes report: fire-and-forget so schedule build never waits on
                // it; failures are silent (retried next run) and nothing downstream reads it.
                _ = ReportCameraModesIfChangedAsync(settings, apiService, token);
                try {
                    var response = await apiService.ListTargetsAsync(settings.SyncToken, "Active", token);
                    if (response.Success && response.Targets != null) {
                        targets = response.Targets;
                        TargetCacheService.SaveFromCloud(targets);   // raw cloud counts to disk, capture ledger applied to `targets`
                        fetchSourceDesc = $"fetched from cloud {DateTime.Now:MMM d, h:mm tt}";
                        Logger.Info($"AstroPM | Fetched {targets.Count} targets from cloud");
                    } else if (response.AuthFailed) {
                        // The token is dead (trial→paid replaced it, licence deactivated, key
                        // removed). This is NOT a transport blip: falling back to the cache here
                        // ran last week's targets forever and re-imaged completed ones while the
                        // desktop pushed progress to the NEW token. Stop and say so.
                        Logger.Error($"AstroPM | Sync token rejected by cloud ({response.Message}) — not using cached targets");
                        Notification.ShowError(
                            "Astro PM: the sync token was rejected by the cloud (it may have been replaced when the " +
                            "licence changed). Open Astro PM on the desktop, copy the current sync token from " +
                            "Settings, and paste it into the plugin Options. No targets will run until then.");
                        return;
                    } else {
                        Logger.Warning($"AstroPM | Cloud error: {response.Message}, falling back to cache");
                    }
                } catch (Exception ex) {
                    Logger.Warning($"AstroPM | Cloud fetch failed ({ex.Message}), falling back to cache");
                }
            } else {
                Logger.Info("AstroPM | Offline/Vacation Mode — skipping cloud fetch, using cached targets.");
            }

            if (targets == null) {
                var cache = TargetCacheService.Load();
                // Online mode only ever means "the cloud was unreachable just now" — a cache older
                // than a week is stale project state, not a safe fallback. Offline/Vacation Mode
                // is the user's explicit choice and keeps using whatever they last synced.
                if (cache != null && !settings.OfflineMode
                    && DateTime.UtcNow - cache.FetchedUtc > TargetCacheService.MaxOnlineFallbackAge) {
                    var age = TargetCacheService.AgeDescription(cache.FetchedUtc);
                    Logger.Error($"AstroPM | Cloud unreachable and cached targets are {age} old — too stale to run");
                    Notification.ShowError(
                        $"Astro PM: the cloud is unreachable and the cached target list is {age} old. " +
                        "Check the rig's internet connection; cached targets older than " +
                        $"{TargetCacheService.MaxOnlineFallbackAge.TotalDays:F0} days are not used unless Offline/Vacation Mode is on.");
                    return;
                }
                if (cache != null) {
                    targets = cache.Targets;
                    var age = TargetCacheService.AgeDescription(cache.FetchedUtc);
                    fetchSourceDesc = settings.OfflineMode
                        ? $"Offline/Vacation Mode — cache from {age}"
                        : $"cloud unavailable — cache from {age}";
                    Logger.Info($"AstroPM | Using cached targets (fetched {age})");
                } else {
                    Notification.ShowError("Astro PM: No target data available from cloud or cache.");
                    return;
                }
            }


            targets = targets.Where(t => t.Panels != null && t.Panels.Any(p =>
                p.ExposureSets != null && p.ExposureSets.Any(es =>
                    SessionScheduler.EffectiveRemainingSubs(es, settings.OvershootPercent) > 0))).ToList();

            // Active projects only — re-filter here even though we request "Active" from the server,
            // because the cache (used offline) can hold on-hold/planning/old targets: the simulator and
            // Options fetch ALL statuses, so the server-side filter doesn't protect the cached path.
            targets = targets.Where(t => string.Equals(t.Status, "Active", StringComparison.OrdinalIgnoreCase)).ToList();

            if (!string.IsNullOrEmpty(settings.LocationFilter))
                targets = targets.Where(t => string.Equals(t.LocationName, settings.LocationFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrEmpty(settings.TelescopeFilter))
                targets = targets.Where(t => string.Equals(t.TelescopeName, settings.TelescopeFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrEmpty(settings.CameraFilter))
                targets = targets.Where(t => string.Equals(t.CameraName, settings.CameraFilter, StringComparison.OrdinalIgnoreCase)).ToList();

            CloudFetchStatus = $"{targets.Count} targets — {fetchSourceDesc}";

            if (targets.Count == 0) {
                var filterDesc = new List<string>();
                if (!string.IsNullOrEmpty(settings.SimStatusFilter)) filterDesc.Add(settings.SimStatusFilter);
                if (!string.IsNullOrEmpty(settings.SimLocationFilter)) filterDesc.Add(settings.SimLocationFilter);
                if (!string.IsNullOrEmpty(settings.SimTelescopeFilter)) filterDesc.Add(settings.SimTelescopeFilter);
                var filterStr = filterDesc.Count > 0 ? $" ({string.Join(", ", filterDesc)})" : "";
                Notification.ShowWarning($"Astro PM: No targets with remaining exposures{filterStr}. Check simulator filters.");
                return;
            }

            foreach (var t in targets)
                global::NINA.Core.Utility.Logger.Info($"AstroPM | Target: {t.TargetName} loc={t.LocationName} scope={t.TelescopeName} panels={t.Panels?.Count ?? 0} remaining={t.Panels?.Sum(p => p.ExposureSets?.Sum(es => es.Remaining) ?? 0) ?? 0}");
            global::NINA.Core.Utility.Logger.Info($"AstroPM | Settings: Strategy={settings.Strategy} SortChain={settings.SortChain} MosaicPanelPref={settings.MosaicPanelPreference} Bonus={settings.BonusEnabled} Dither={settings.DitherEnabled}/{settings.DitherEvery} FilterSwitch={settings.FilterSwitchEnabled}/{settings.FilterSwitchCount} Tolerance={settings.FilterSwitchTolerance:P0}");

            progress?.Report(new ApplicationStatus { Status = $"Astro PM: Calculating schedule for {targets.Count} targets..." });

            double latDeg = _profileService.ActiveProfile.AstrometrySettings.Latitude;
            double lonDeg = _profileService.ActiveProfile.AstrometrySettings.Longitude;

            if (Math.Abs(latDeg) < 0.001 && Math.Abs(lonDeg) < 0.001) {
                Notification.ShowWarning("Astro PM: Observatory location not set in NINA profile.");
                return;
            }

            var tz = TimeZoneInfo.Local;
            // An observing night spans two calendar dates (evening → dawn). Nights are
            // identified noon-to-noon (same convention as IsStaleSession and the desktop
            // app): any rebuild before local noon — a post-midnight continuation OR a
            // post-dawn crash recovery — keys to the night just run, so flats tracking
            // recovers instead of resetting under it. Dawn is always before noon, so a
            // hard-coded dawn hour is unnecessary. After noon we schedule tonight.
            var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var date = CurrentNightKey();
            _scheduleNightDate = date;
            SyncFlatTrackingForNight(date);
            global::NINA.Core.Utility.Logger.Info(
                $"AstroPM | BuildSchedule: date={date:yyyy-MM-dd} (local={localNow:HH:mm}), lat={latDeg:F4}, lon={lonDeg:F4}, tz={tz.Id}, targets={targets.Count}");
            var slots = SessionScheduler.BuildTimeSlots(date, latDeg, lonDeg, tz);

            // NINA's custom horizon (.hrz) — when loaded in Options → General, targets
            // must clear the obstruction line at their azimuth, matching the desktop sim.
            var customHorizon = HorizonProfile.LoadFromNinaProfile();
            global::NINA.Core.Utility.Logger.Info(customHorizon != null
                ? $"AstroPM | Custom horizon active: {customHorizon.Points.Count} points from NINA profile horizon file"
                : "AstroPM | No custom horizon file in NINA profile (flat min-altitude only)");

            var profiles = SessionScheduler.BuildTargetProfiles(targets, slots, latDeg, lonDeg, settings.MosaicPanelPreference, customHorizon, tz,
                overshootPercent: settings.OvershootPercent, minTimeTolerance: settings.MinTimeTolerance);

            foreach (var p in profiles)
                global::NINA.Core.Utility.Logger.Info($"AstroPM | Profile: {p.DisplayName} PanelIdx={p.PanelIndex} LA={p.RemainingLunarFreeSec / 60:F0}m NonLA={p.RemainingNonLunarSec / 60:F0}m window={p.WindowStartSlot}-{p.WindowEndSlot}");

            if (profiles.Count == 0) {
                Notification.ShowWarning("Astro PM: No targets are visible tonight from this location.");
                return;
            }

            foreach (var p in profiles) p.AllocatedSec = 0;

            // Priority order from Project.Priority (1 = highest, 0 = unset → last),
            // synced from the Astro PM cloud. Drives the Manual Priority strategy.
            // Ties break by project name to match the desktop app — the cloud list is
            // updated_at-ordered, so a positional tie-break would reorder on every re-push.
            var order = Enumerable.Range(0, profiles.Count)
                .OrderBy(i => profiles[i].Target.Priority == 0 ? int.MaxValue : profiles[i].Target.Priority)
                .ThenBy(i => profiles[i].Target.ProjectName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => profiles[i].PanelIndex ?? -1) // mosaic panels: explicit P1→P2 order
                .ThenBy(i => i)
                .ToList();
            var matrix = ScheduleEngine.BuildMatrix(slots, profiles, order, settings.MinTimeTolerance);

            if (matrix.FirstUsableSlot < 0) {
                Notification.ShowWarning("Astro PM: No usable time window for any target.");
                return;
            }

            var sortChain = ParseSortChain(settings.SortChain);
            var moonDownChain = new List<SortCriteria> { SortCriteria.MostLaWork };
            foreach (var c in sortChain)
                if (c != SortCriteria.MostLaWork) moonDownChain.Add(c);

            ScheduleEngine.ComputeOverlap(matrix);
            ScheduleEngine.OrganizeMoonBlocks(matrix);

            if (Enum.TryParse<ImagingStrategy>(settings.Strategy, out var strategy)
                && strategy == ImagingStrategy.ManualPriority)
                ScheduleEngine.PaintSlotsGreedy(matrix, order, settings.BonusEnabled);
            else
                ScheduleEngine.PaintSlots(matrix, sortChain, moonDownChain, settings.BonusEnabled);

            var state = new ScheduleSessionState();
            if (settings.OvershootPercent > 0) state.OvershootFraction = settings.OvershootPercent / 100.0;
            var log = ScheduleEngine.WalkToLog(matrix, state, tz,
                settings.DitherEnabled, settings.DitherEvery, settings.FilterSwitchEnabled, settings.FilterSwitchCount, sortChain,
                bonusEnabled: settings.BonusEnabled,
                filterSwitchTolerance: settings.FilterSwitchTolerance);

            _lastLog = log;
            _lastSlots = slots;
            _lastProfiles = profiles;
            _blocks = ParseBlocks(log, profiles);
            _currentBlockIndex = 0;

            var endEntry = log.LastOrDefault(e => e.Command == "End");
            _sessionEndUtc = endEntry?.UtcTime ?? slots.Last().UtcStart.AddSeconds(300);

            _scheduleBuilt = true;
            HasChartData = true;
            LogEntries = new ObservableCollection<SimLogEntry>(log);
            BuildBlockSummaries();

            global::NINA.Core.Utility.Logger.Info(
                $"AstroPM | Schedule built: {_blocks.Count} blocks, session {slots.First().UtcStart:HH:mm}–{_sessionEndUtc:HH:mm} UTC");

            int imageCount = log.Count(e => e.Command == "Image" || e.Command == "Bonus");
            int targetCount = profiles.Count(p => p.AllocatedSec > 0);
            Notification.ShowSuccess($"Astro PM: Schedule built — {imageCount} subs across {targetCount} targets");

            foreach (var entry in log) {
                global::NINA.Core.Utility.Logger.Info($"AstroPM Schedule | {FormatLogLine(entry)}");
            }
        }

        private async Task ExecuteNextBlock(IProgress<ApplicationStatus> progress, CancellationToken token) {
            // Flag the active execution window so HasBlocksRemaining can't flip false
            // mid-block: the end-of-night grace sub intentionally runs past _sessionEndUtc,
            // and NINA's condition watchdog cancels the running instruction the moment the
            // loop condition goes false — slewing/parking into the still-integrating final
            // frame (streaked stars) and starting flats before the last light is done.
            _executingBlock = true;
            try {
                await ExecuteNextBlockCore(progress, token);
            } finally {
                _executingBlock = false;
            }
        }

        private volatile bool _executingBlock;

        private async Task ExecuteNextBlockCore(IProgress<ApplicationStatus> progress, CancellationToken token) {
            double latDeg = _profileService.ActiveProfile.AstrometrySettings.Latitude;
            double lonDeg = _profileService.ActiveProfile.AstrometrySettings.Longitude;

            // Skip past any blocks whose time window has already elapsed
            while (_currentBlockIndex < _blocks.Count) {
                var candidate = _blocks[_currentBlockIndex];
                if (DateTime.UtcNow < candidate.UtcEnd) break;
                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Skipping past block: {candidate.TargetName} (ended {candidate.UtcEnd:HH:mm} UTC, now {DateTime.UtcNow:HH:mm} UTC)");
                if (_blockSummaries != null && _currentBlockIndex < _blockSummaries.Count)
                    _blockSummaries[_currentBlockIndex].Status = "skipped";
                _currentBlockIndex++;
            }

            if (_currentBlockIndex >= _blocks.Count || DateTime.UtcNow >= _sessionEndUtc) {
                progress?.Report(new ApplicationStatus { Status = "Astro PM: Session complete." });
                UpdateLiveStatus("Complete", null);
                UpdateCurrentBlock();
                var reason = _currentBlockIndex >= _blocks.Count ? "all blocks complete" : $"past session end ({_sessionEndUtc:HH:mm} UTC)";
                global::NINA.Core.Utility.Logger.Info($"AstroPM | Session ended — {reason}. Processed {_currentBlockIndex}/{_blocks.Count} blocks.");
                return;
            }

            var block = _blocks[_currentBlockIndex];

            // A block with no scheduled exposures (the planner emitted a Slew the walk
            // couldn't fill) would slew + center + guide for nothing — skip it outright.
            if (!block.Entries.Any(e => e.Command == "Image" || e.Command == "Bonus")) {
                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Skipping empty block (no scheduled exposures): {block.TargetName} ({block.UtcStart:HH:mm}–{block.UtcEnd:HH:mm} UTC)");
                if (_blockSummaries != null && _currentBlockIndex < _blockSummaries.Count)
                    _blockSummaries[_currentBlockIndex].Status = "skipped";
                _currentBlockIndex++;
                return;
            }

            UpdateCurrentBlock();
            SetTargetFromBlock(block);

            global::NINA.Core.Utility.Logger.Info(
                $"AstroPM | Block {_currentBlockIndex + 1}/{_blocks.Count}: {block.TargetName} | " +
                $"Window: {block.UtcStart:HH:mm}–{block.UtcEnd:HH:mm} UTC | " +
                $"RA={block.RaHours:F4}h Dec={block.DecDegrees:F4}° | " +
                $"Exposures: {block.Entries.Count(e => e.Command == "Image" || e.Command == "Bonus")} subs");

            // Wait for block start time BEFORE checking viability.
            // (Can't check sun/target altitude at 10 AM for a 9 PM block!)
            if (DateTime.UtcNow < block.UtcStart && !_skipWait) {
                if (_blockSummaries != null && _currentBlockIndex < _blockSummaries.Count)
                    _blockSummaries[_currentBlockIndex].Status = "waiting...";

                // Wait in a loop so we can break out if _skipWait is set
                while (DateTime.UtcNow < block.UtcStart && !_skipWait) {
                    token.ThrowIfCancellationRequested();
                    var waitSec = (block.UtcStart - DateTime.UtcNow).TotalSeconds;
                    progress?.Report(new ApplicationStatus {
                        Status = $"Astro PM: Waiting {waitSec / 60.0:F0} min for {block.TargetName}..."
                    });
                    UpdateLiveStatus("Wait", block);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(waitSec, 10)), token);
                }

                if (_skipWait) {
                    global::NINA.Core.Utility.Logger.Info($"AstroPM | Wait skipped for block: {block.TargetName}");
                }
            }

            // Custom horizon from the NINA profile — the engine planned against it, so the
            // runtime checks honor it too (null = flat min-altitude only).
            var customHorizon = HorizonProfile.LoadFromNinaProfile();

            // Check constraints — bypass viability when the wait was skipped (e.g. Skip Block)
            if (!_skipWait && !IsTargetViable(block, latDeg, lonDeg, customHorizon)) {
                global::NINA.Core.Utility.Logger.Info($"AstroPM | Target constraints failed, skipping: {block.TargetName}");
                if (_blockSummaries != null && _currentBlockIndex < _blockSummaries.Count)
                    _blockSummaries[_currentBlockIndex].Status = "skipped";
                _currentBlockIndex++;
                return;
            }

            // A block the user forced past its wait (Skip Wait) also skips the per-sub
            // constraint check below — they've overridden the planner on purpose.
            bool constraintsOverridden = _skipWait;

            // Reset skip flag after passing the wait/viability gate
            _skipWait = false;

            var actionEntries = block.Entries
                .Where(e => e.Command == "Image" || e.Command == "Bonus" || e.Command == "Dither")
                .ToList();

            global::NINA.Core.Utility.Logger.Info(
                $"AstroPM | Starting block: {block.TargetName} until {block.UtcEnd:HH:mm} ({actionEntries.Count} scheduled actions)");

            // ── Phase 1: Slew + center + guide (with retry/skip on failure) ──
            if (_blockSummaries != null && _currentBlockIndex < _blockSummaries.Count)
                _blockSummaries[_currentBlockIndex].Status = "slewing...";

            Action<string, TargetBlock> updateSimple = (cmd, b) => UpdateLiveStatus(cmd, b);

            // Escalating backoff between center attempts. The first few retries fire quickly to
            // shrug off transient blips (dome not yet synced, guider not settled, a momentary
            // slew glitch); the later ones widen out for conditions that need tens of minutes to
            // clear — a cloud band starving the plate solve of stars ("not enough stars"), or a
            // mount left parked by a safety recovery that the user unparks after seeing the
            // failure notification. Capped at 10 min per wait, ~34 min total; the block-end guard
            // below abandons the ladder early once the window can't fit another retry, so long
            // tails never overrun short blocks. The array length + 1 is the total attempt count.
            int[] slewRetryDelaysSec = { 15, 15, 30, 60, 120, 300, 300, 600, 600 };
            int maxSlewRetries = slewRetryDelaysSec.Length + 1; // 10 attempts
            bool slewSucceeded = false;

            for (int attempt = 1; attempt <= maxSlewRetries; attempt++) {
                try {
                    var slewItem = new AstroPMSlewCenterItem(block,
                        _profileService, _telescopeMediator, _imagingMediator, _rotatorMediator,
                        _filterWheelMediator, _guiderMediator, _domeMediator, _domeFollower,
                        _plateSolverFactory, _windowServiceFactory, updateSimple);
                    await slewItem.Execute(progress, token);
                    slewSucceeded = true;
                    break;
                } catch (OperationCanceledException) {
                    throw; // User cancelled — don't retry
                } catch (Exception ex) {
                    global::NINA.Core.Utility.Logger.Warning(
                        $"AstroPM | Slew failed for {block.TargetName} (attempt {attempt}/{maxSlewRetries}): {ex.Message}");

                    if (attempt < maxSlewRetries) {
                        int delaySec = slewRetryDelaysSec[attempt - 1];
                        // Don't bother waiting if the retry would land past the block's window
                        if (DateTime.UtcNow.AddSeconds(delaySec) >= block.UtcEnd) {
                            global::NINA.Core.Utility.Logger.Warning(
                                $"AstroPM | Block window for {block.TargetName} ends before next retry — giving up early");
                            break;
                        }
                        string delayLabel = delaySec >= 60 ? $"{delaySec / 60.0:0.#} min" : $"{delaySec}s";
                        UpdateLiveStatus("Slew Error", block);
                        progress?.Report(new ApplicationStatus {
                            Status = $"Astro PM: Slew failed — retrying in {delayLabel} ({attempt}/{maxSlewRetries})..."
                        });
                        await Task.Delay(TimeSpan.FromSeconds(delaySec), token);
                    }
                }
            }

            if (!slewSucceeded) {
                global::NINA.Core.Utility.Logger.Error(
                    $"AstroPM | Slew failed after {maxSlewRetries} attempts — skipping block: {block.TargetName}");
                global::NINA.Core.Utility.Notification.Notification.ShowError(
                    $"AstroPM: Skipping {block.TargetName} — slew failed after {maxSlewRetries} attempts");
                FinishBlock(block, skipped: true);
                _currentBlockIndex++;
                return;
            }

            token.ThrowIfCancellationRequested();
            if (_skipBlock) { FinishBlock(block, skipped: true); _currentBlockIndex++; return; }

            // ── Fire "Before Target" triggers ──
            // Fires with the scope already slewed/centered/rotated on the new target, before
            // guiding and imaging start — so dropped instructions (autofocus, settle waits,
            // covers, etc.) run on-target rather than while still pointed at the old one.
            // Walk every ancestor container (like NINA's SequentialStrategy) so the
            // trigger also fires from Global Triggers, not just our immediate parent.
            for (var c = Parent as SequenceContainer; c != null; c = c.Parent as SequenceContainer) {
                foreach (var trigger in c.GetTriggersSnapshot()) {
                    if (trigger is AstroPMBeforeTargetTrigger beforeTarget)
                        await beforeTarget.Fire(block, progress, token);
                }
            }

            token.ThrowIfCancellationRequested();
            if (_skipBlock) { FinishBlock(block, skipped: true); _currentBlockIndex++; return; }

            var guideItem = new AstroPMStartGuidingItem(block, _guiderMediator, updateSimple);
            await guideItem.Execute(progress, token);

            // ── Phase 2: Time-aware exposure loop ──
            // Instead of running exposures sequentially, we check the current UTC time against
            // the schedule and jump to whichever entry should be running NOW. This keeps the
            // actual filter in sync with the graph after any delays (autofocus, safety holds, etc.).
            var parentContainer = Parent as SequenceContainer;
            SequenceItem previousExposure = null;
            int blockIdx = _currentBlockIndex;
            void OnCaptured() {
                if (_blockSummaries != null && blockIdx < _blockSummaries.Count)
                    _blockSummaries[blockIdx].CapturedCount++;
            }

            if (_blockSummaries != null && _currentBlockIndex < _blockSummaries.Count)
                _blockSummaries[_currentBlockIndex].Status = "imaging...";

            var filterImageCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            int lastExecutedIndex = -1;

            // Playback mode (read once per block, so a resume after a hold honors the
            // current setting). Sequential: walk this block's entries strictly in order and
            // never skip — after autofocus/hold delays we resume where we left off rather
            // than jumping ahead, so a block simply captures fewer subs if it runs long.
            // Time-Aware (default): jump to the entry that should be running now — right for
            // long safety closures. The block.UtcEnd guard stops both modes at the boundary.
            Enum.TryParse<PlaybackMode>(AstroPMSettings.Load().PlaybackMode, out var playbackMode);
            bool sequential = playbackMode == PlaybackMode.Sequential;
            global::NINA.Core.Utility.Logger.Info(
                $"AstroPM | Block {block.TargetName}: playback mode = {(sequential ? "Sequential" : "Time-Aware")}");

            // Per-sub constraint check: the block-start gate alone let a late-running block keep
            // imaging after its target sank below min altitude (10/3, Lion Nebula on Dome A —
            // Sequential playback ~50 min behind plan, block end = session end at dawn). Re-check
            // altitude/horizon/darkness before every sub so the block ends and the sequence
            // falls through to flats / end-of-night instead.
            bool StillViable(string when) {
                if (constraintsOverridden || IsTargetViable(block, latDeg, lonDeg, customHorizon)) return true;
                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Ending block {when}: {block.TargetName} no longer meets its constraints");
                return false;
            }

            while (DateTime.UtcNow < block.UtcEnd && !_skipBlock) {
                token.ThrowIfCancellationRequested();
                if (!StillViable("before next sub")) break;

                int targetIndex;
                if (sequential) {
                    // Strictly the next entry in order; skip Dither markers (applied below).
                    targetIndex = lastExecutedIndex + 1;
                    while (targetIndex < actionEntries.Count && actionEntries[targetIndex].Command == "Dither") {
                        targetIndex++;
                    }
                    if (targetIndex >= actionEntries.Count) break;
                } else {
                    // Find the schedule entry we should be on right now.
                    // Walk backward from end to find the last entry whose UtcTime <= now.
                    var now = DateTime.UtcNow;
                    targetIndex = -1;
                    for (int i = actionEntries.Count - 1; i >= 0; i--) {
                        if (actionEntries[i].UtcTime <= now) {
                            targetIndex = i;
                            break;
                        }
                    }

                    // If we're before the first entry, wait a moment and retry
                    if (targetIndex < 0) {
                        await Task.Delay(1000, token);
                        continue;
                    }

                    // If we land on a Dither, skip forward to the next exposure
                    while (targetIndex < actionEntries.Count && actionEntries[targetIndex].Command == "Dither") {
                        targetIndex++;
                    }
                    if (targetIndex >= actionEntries.Count) break;

                    // If we've already executed this entry or a later one, we need the NEXT entry
                    if (targetIndex <= lastExecutedIndex) {
                        targetIndex = lastExecutedIndex + 1;
                        // Skip dithers again
                        while (targetIndex < actionEntries.Count && actionEntries[targetIndex].Command == "Dither") {
                            targetIndex++;
                        }
                        if (targetIndex >= actionEntries.Count) break;

                        // If the next entry hasn't started yet, wait for it
                        if (actionEntries[targetIndex].UtcTime > now) {
                            var waitMs = (actionEntries[targetIndex].UtcTime - now).TotalMilliseconds;
                            if (waitMs > 500) {
                                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(waitMs, 5000)), token);
                                continue;
                            }
                        }
                    }

                    // Log if we skipped entries to catch up
                    if (targetIndex > lastExecutedIndex + 1 && lastExecutedIndex >= 0) {
                        int skipped = targetIndex - lastExecutedIndex - 1;
                        global::NINA.Core.Utility.Logger.Info(
                            $"AstroPM | Time-sync: skipped {skipped} entries to stay on schedule " +
                            $"(jumped from #{lastExecutedIndex + 1} to #{targetIndex + 1} — {actionEntries[targetIndex].Filter})");
                    }
                }

                var entry = actionEntries[targetIndex];
                var (filterName, exposureSec, gain, offset, binX, binY, readoutMode) = ParseExposureEntry(entry);

                if (!filterImageCount.ContainsKey(filterName))
                    filterImageCount[filterName] = 0;
                int filterSub = filterImageCount[filterName] + 1;

                var exposureItem = new AstroPMTakeExposureItem(block, filterName, exposureSec,
                    gain, offset, binX, binY, readoutMode, filterSub,
                    block.UtcEnd, _sessionEndUtc,
                    _profileService, _filterWheelMediator, _imagingMediator,
                    _cameraMediator, _imageSaveMediator, _imageHistoryVM,
                    (cmd, b, f, e, s, go) => UpdateLiveStatus(cmd, b, filter: f, exposure: e, sub: s, gainOffset: go),
                    () => {
                        filterImageCount[filterName] = filterSub;
                        OnCaptured();
                        RecordFlatSpec(block, filterName, gain, offset, binX, binY);
                        RecordCaptureToLedger(block, entry.Panel, filterName, exposureSec, gain, offset, binX, binY);
                    });

                // Anchor the exposure item's Parent to us (the IDeepSkyObjectContainer that holds the
                // scheduled Target). NINA's SequenceContainer.RunTriggers resolves a trigger's context
                // as `nextItem?.Parent ?? previousItem?.Parent ?? this`, and MeridianFlipTrigger.Execute
                // pulls its flip + post-flip recenter coordinates from RetrieveContextCoordinates(context).
                // Without this, nextItem.Parent is null so context falls through to the PARENT container,
                // whose upward walk never descends into us — the flip then logs "No target information
                // available for flip" and falls back to live telescope coordinates (only correct by luck).
                // Pointing nextItem.Parent at us makes the flip and recenter use the real target RA/Dec.
                exposureItem.AttachNewParent(this);

                // Switch filter before triggers so NINA's AutofocusAfterFilterChange
                // sees the new filter on the physical wheel when it evaluates.
                await exposureItem.SwitchFilterAsync(progress, token);

                // Dither if the schedule has a Dither entry between the last exposure and this one
                if (lastExecutedIndex >= 0) {
                    bool needsDither = false;
                    for (int d = lastExecutedIndex + 1; d < targetIndex; d++) {
                        if (actionEntries[d].Command == "Dither") { needsDither = true; break; }
                    }
                    if (needsDither) {
                        var ditherItem = new AstroPMDitherItem(block, _guiderMediator, updateSimple);
                        await ditherItem.Execute(progress, token);
                    }
                }

                // Fire pre-triggers (autofocus, meridian flip, center-after-drift, etc.)
                // Walk every ancestor container like NINA's SequentialStrategy does, so
                // Global Triggers (remote play/pause, meridian flip, ...) fire between
                // our exposures too — not only the immediate parent's triggers.
                if (parentContainer != null) {
                    try {
                        UpdateLiveStatus("Triggers", block);
                        global::NINA.Core.Utility.Logger.Info(
                            $"AstroPM | Running pre-triggers: {block.TargetName} {filterName} #{targetIndex + 1}");
                        for (var c = parentContainer; c != null; c = c.Parent as SequenceContainer) {
                            await c.RunTriggers(previousExposure ?? this, exposureItem, progress, token);
                        }
                    } catch (Exception ex) {
                        global::NINA.Core.Utility.Logger.Warning(
                            $"AstroPM | Trigger error before exposure {block.TargetName} #{targetIndex + 1}: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                // Triggers (autofocus, flip, recenter) can take minutes — re-check before exposing.
                if (!StillViable("after pre-triggers")) break;

                // Take the exposure
                await exposureItem.Execute(progress, token);

                // Fire post-triggers (ancestor walk — see pre-triggers above)
                if (parentContainer != null) {
                    try {
                        for (var c = parentContainer; c != null; c = c.Parent as SequenceContainer) {
                            await c.RunTriggersAfter(exposureItem, this, progress, token);
                        }
                    } catch (Exception ex) {
                        global::NINA.Core.Utility.Logger.Warning(
                            $"AstroPM | Trigger error after exposure {block.TargetName} #{targetIndex + 1}: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                previousExposure = exposureItem;
                lastExecutedIndex = targetIndex;
            }

            if (_skipBlock) {
                FinishBlock(block, skipped: true);
            } else {
                FinishBlock(block, skipped: false);
            }

            // ── Fire "After Target" triggers ── (ancestor walk, incl. Global Triggers)
            for (var c = parentContainer; c != null; c = c.Parent as SequenceContainer) {
                foreach (var trigger in c.GetTriggersSnapshot()) {
                    if (trigger is AstroPMAfterTargetTrigger afterTarget) {
                        try {
                            await afterTarget.Fire(block, progress, token);
                        } catch (Exception ex) {
                            global::NINA.Core.Utility.Logger.Warning(
                                $"AstroPM | After Target trigger error for {block.TargetName}: {ex.Message}");
                        }
                    }
                }
            }

            _currentBlockIndex++;
        }

        private void FinishBlock(TargetBlock block, bool skipped) {
            // Both skip flags are cleared here. Skip Block pressed during the pre-block wait used
            // to leave _skipWait set after the (early-returned) block, so the NEXT block skipped
            // its wait and viability gate too and slewed/imaged early. The early-return callers
            // also advance _currentBlockIndex themselves — without that the same block was
            // re-entered and waited on again.
            _skipWait = false;
            if (skipped) {
                global::NINA.Core.Utility.Logger.Info($"AstroPM | Block skipped: {block.TargetName}");
                if (_blockSummaries != null && _currentBlockIndex < _blockSummaries.Count)
                    _blockSummaries[_currentBlockIndex].Status = "skipped";
                _skipBlock = false;
            } else {
                global::NINA.Core.Utility.Logger.Info($"AstroPM | Block complete: {block.TargetName}");
                if (_blockSummaries != null && _currentBlockIndex < _blockSummaries.Count)
                    _blockSummaries[_currentBlockIndex].Status = "completed";
            }
        }

        private void SetTargetFromBlock(TargetBlock block) {
            var inputCoords = new InputCoordinates(
                new Coordinates(
                    Angle.ByHours(block.RaHours),
                    Angle.ByDegree(block.DecDegrees),
                    Epoch.J2000));

            var astroSettings = _profileService.ActiveProfile.AstrometrySettings;
            var target = new InputTarget(
                Angle.ByDegree(astroSettings.Latitude),
                Angle.ByDegree(astroSettings.Longitude),
                astroSettings.Horizon);
            target.TargetName = block.TargetName;
            target.InputCoordinates = inputCoords;
            target.PositionAngle = block.RotationDeg;

            // The InputTarget ctor seeds a DeepSkyObject with EMPTY (0,0) coordinates. Consumers
            // that read Target.DeepSkyObject (framing, some third-party triggers/instructions, UI)
            // would otherwise see the wrong position — keep it in sync with InputCoordinates.
            if (target.DeepSkyObject != null) {
                target.DeepSkyObject.Name = block.TargetName;
                target.DeepSkyObject.Coordinates = inputCoords.Coordinates;
            }
            target.Expanded = true;

            Target = target;

            // Push the new target into the parent triggers/instruction blocks that consume it.
            var injector = new CoordinatesInjector(target);
            var container = Parent as SequenceContainer;
            while (container != null) {
                foreach (var trigger in container.GetTriggersSnapshot()) {
                    if (trigger is CenterAfterDriftTrigger driftTrigger) {
                        // AttachNewParent makes the trigger re-discover us as the IDeepSkyObjectContainer
                        // via AfterParentChanged; we also set Coordinates explicitly as belt-and-suspenders.
                        // Clone so the trigger can't mutate our target's coordinates, and
                        // SequenceBlockInitialize resets its drift baseline for the new target (else it
                        // carries the previous target's plate-solve reference into this block).
                        driftTrigger.AttachNewParent(this);
                        driftTrigger.Coordinates = target.InputCoordinates.Clone();
                        driftTrigger.Inherited = true;
                        driftTrigger.SequenceBlockInitialize();
                    }

                    // Users may drop coordinate-aware instructions (Center, Center & Rotate, Slew to
                    // RA/Dec — native or via Sequencer Powerups) into our custom "Before/After Each
                    // Exposure" and "Before/After Target" blocks. Those don't inherit from a static DSO
                    // container, so inject the live target coordinates into their instruction lists.
                    ISequenceContainer runner = null;
                    switch (trigger) {
                        case AstroPMBeforeExposureTrigger t: runner = t.TriggerRunner; break;
                        case AstroPMAfterExposureTrigger t: runner = t.TriggerRunner; break;
                        case AstroPMBeforeTargetTrigger t: runner = t.TriggerRunner; break;
                        case AstroPMAfterTargetTrigger t: runner = t.TriggerRunner; break;
                    }
                    if (runner != null) injector.Inject(runner);
                }
                container = container.Parent as SequenceContainer;
            }

            global::NINA.Core.Utility.Logger.Info($"AstroPM | Target set for triggers: {block.TargetName} RA={block.RaHours:F4}h Dec={block.DecDegrees:F3}°");
        }

        /// <summary>Advisory camera-modes sync: push the connected camera's readout-mode enumeration
        /// to the imaging system's cloud row so the desktop app can offer it as a one-click
        /// suggestion. Push only when the list has 2+ entries (modeless ZWO/DSLR rigs report a
        /// single "Default" — nothing to sync) and only when it changed since the last successful
        /// report. Never throws; the authoritative mode list stays the user's camera row.</summary>
        private async Task ReportCameraModesIfChangedAsync(AstroPMSettings settings, AstroPMApiService apiService, CancellationToken token) {
            try {
                var info = _cameraMediator?.GetInfo();
                if (info == null || !info.Connected) return;
                var modes = info.ReadoutModes?.Where(m => !string.IsNullOrWhiteSpace(m)).ToList();
                if (modes == null || modes.Count < 2) return;
                if (string.IsNullOrEmpty(settings.SelectedImagingSystem)) return;

                // Order matters (position = NINA readout-mode index), so the fingerprint is the
                // ordered list itself. Human-readable on purpose — visible in settings.json.
                var fingerprint = string.Join("|", modes);
                if (fingerprint == settings.LastReportedModesHash) return;

                bool stored = await apiService.ReportCameraModesAsync(
                    settings.SyncToken, settings.SelectedImagingSystem, info.Name, modes, token);
                if (stored) {
                    settings.LastReportedModesHash = fingerprint;
                    settings.Save();
                    Logger.Info($"AstroPM | Reported {modes.Count} camera readout modes for '{settings.SelectedImagingSystem}' ({info.Name})");
                } else {
                    Logger.Debug("AstroPM | Camera readout-mode report not stored (offline or system not on cloud) — will retry next run");
                }
            } catch (OperationCanceledException) {
                // Sequence stopped mid-report — advisory, drop it.
            } catch (Exception ex) {
                Logger.Debug($"AstroPM | Camera readout-mode report failed (advisory, ignored): {ex.Message}");
            }
        }

        private bool IsTargetViable(TargetBlock block, double latDeg, double lonDeg, HorizonProfile customHorizon = null) {
            var now = DateTime.UtcNow;
            if (now >= block.UtcEnd) {
                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Viability failed: {block.TargetName} — past block end ({block.UtcEnd:HH:mm} UTC)");
                return false;
            }

            if (block.Profile == null) return true;

            double altitude = AstroCalculator.TargetAltitudeAtTime(now, block.RaHours, block.DecDegrees, latDeg, lonDeg);
            double sunAlt = AstroCalculator.SunAltitudeAtTime(now, latDeg, lonDeg);

            var c = block.Profile.Constraints;
            if (c == null) {
                bool viable = altitude > 10 && sunAlt < -6;
                if (!viable)
                    global::NINA.Core.Utility.Logger.Info(
                        $"AstroPM | Viability failed: {block.TargetName} — alt={altitude:F1}° (min 10°), sun={sunAlt:F1}° (max -6°)");
                return viable;
            }

            bool isDark = sunAlt < c.SunAltitudeThreshold;
            bool aboveMinAlt = altitude >= c.MinTargetAltitude;
            string horizonNote = "";
            if (aboveMinAlt && customHorizon != null) {
                // Same rule as SessionScheduler.BuildTargetProfiles: must clear the custom horizon too.
                double az = AstroCalculator.TargetAzimuthAtTime(now, block.RaHours, block.DecDegrees, latDeg, lonDeg);
                double hrzAlt = customHorizon.AltitudeAt(az);
                aboveMinAlt = altitude >= hrzAlt;
                horizonNote = $", horizon {hrzAlt:F1}° @ az {az:F0}°";
            }

            if (!isDark || !aboveMinAlt)
                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Viability failed: {block.TargetName} — alt={altitude:F1}° (min {c.MinTargetAltitude}°{horizonNote}), sun={sunAlt:F1}° (max {c.SunAltitudeThreshold}°)");

            return isDark && aboveMinAlt;
        }

        /// <summary>Offline/Vacation Mode: count a freshly-captured LIGHT frame against the cached target so
        /// the next night's sim sees reduced remaining. Matches the exposure set by panel + filter + exposure
        /// (disambiguated by gain/offset/bin), increments Accepted/Acquired, and re-saves the cache.</summary>
        /// <summary>Counts a captured light against its exposure set, online or offline. Bumps the
        /// running in-memory copy (so a same-night rebuild still sees tonight's subs) and the
        /// on-disk capture ledger, which survives fetches until the desktop's own count moves —
        /// see TargetCacheService for the reconcile rule.</summary>
        private void RecordCaptureToLedger(TargetBlock block, string panelLabel, string filter, double exposureSec, int gain, int offset, int binX, int binY) {
            try {
                var target = block?.Profile?.Target;
                if (target?.Panels == null || target.Panels.Count == 0) return;

                var panel = target.Panels.FirstOrDefault(p => string.Equals(p.Label, panelLabel, StringComparison.OrdinalIgnoreCase))
                            ?? (target.Panels.Count == 1 ? target.Panels[0] : null);
                if (panel?.ExposureSets == null) return;

                var matches = panel.ExposureSets.Where(es =>
                    string.Equals(es.FilterName, filter, StringComparison.OrdinalIgnoreCase) &&
                    Math.Abs(es.ExposureLengthSec - exposureSec) < 0.5).ToList();
                var es = matches.Count <= 1
                    ? matches.FirstOrDefault()
                    : (matches.FirstOrDefault(e => e.Gain == gain && e.Offset == offset && e.BinningX == binX && e.BinningY == binY) ?? matches[0]);
                if (es == null) return;

                es.AcquiredCount++;
                es.AcceptedCount++;   // optimistic: counts toward Remaining (Planned - Accepted) until the desktop counts the files

                TargetCacheService.RecordCapture(target, panel, es);

                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Capture counted: {target.TargetName}/{panel.Label} {filter} {exposureSec:F0}s → {es.AcceptedCount}/{es.PlannedCount} (remaining {es.Remaining})");
            } catch (Exception ex) {
                global::NINA.Core.Utility.Logger.Warning($"AstroPM | Capture ledger update failed: {ex.Message}");
            }
        }

        // ── Flat Handling ──

        /// <summary>Called from BuildSchedule with the observing-night date. On a new night the
        /// combo list resets; on a same-night rebuild (manual reset, NINA restart) recorded combos
        /// are kept / recovered from disk so end-of-night flats still cover the whole night.</summary>
        private void SyncFlatTrackingForNight(DateTime nightDate) {
            var key = nightDate.ToString("yyyy-MM-dd");
            if (_nightDate == key) return; // same-night rebuild — keep recorded combos
            _nightDate = key;
            lock (_flatSpecs) {
                _flatSpecs.Clear();
                var store = FlatSpecStore.Load();
                // Auto Flats carry-over rides across nights until it runs, then expires.
                lock (_carryOverSpecs) {
                    _carryOverSpecs.Clear();
                    _carryOverNight = "";
                    if (store.CarryOver != null && store.CarryOver.Count > 0) {
                        bool fresh = DateTime.TryParse(store.CarryOverNight, out var cn)
                            && (nightDate - cn).TotalDays <= CarryOverMaxNights;
                        if (fresh) {
                            _carryOverSpecs.AddRange(store.CarryOver);
                            _carryOverNight = store.CarryOverNight;
                            global::NINA.Core.Utility.Logger.Info(
                                $"AstroPM | Flats: {store.CarryOver.Count} combos carried over from {store.CarryOverNight} will be made up at this session's flats pass");
                        } else {
                            global::NINA.Core.Utility.Logger.Warning(
                                $"AstroPM | Flats: dropping {store.CarryOver.Count} carried-over combos from {store.CarryOverNight} — older than {CarryOverMaxNights} nights");
                        }
                    }
                }
                lock (_cloudLedgers) {
                    _cloudLedgers.Clear();
                    if (store.CloudLedgers != null)
                        foreach (var kv in store.CloudLedgers) _cloudLedgers[kv.Key] = kv.Value;
                }
                if (store.NightDate == key) {
                    // NINA restarted mid-night — recover combos captured before the restart
                    _flatSpecs.AddRange(store.Specs);
                    _flatsDone = store.FlatsCompletedUtc.HasValue;
                    global::NINA.Core.Utility.Logger.Info(
                        $"AstroPM | Flats: recovered {store.Specs.Count} filter/rotation combos for night {key}" +
                        (_flatsDone ? " (flats already completed tonight)" : ""));
                } else {
                    _flatsDone = false;
                }
            }
            DropCarryOverIfDisabled("new night");
            // A new observing night: clear the checkmarks/progress last night's pass left
            // on the drop-zone instructions — including nested loop counters, which would
            // otherwise make Trained Flat Exposure skip itself tonight (20/20 from last night).
            ResetRunnerProgress(FlatsSetupRunner);
            ResetRunnerProgress(FlatsRunner);
            ResetRunnerProgress(FlatsTeardownRunner);
            UpdateFlatsSummary();
        }

        /// <summary>Record the full spec of a successful LIGHT capture. Deduped by filter + sky PA +
        /// gain + offset + binning (all part of the trained-flat identity); the rotator's mechanical
        /// position is stored because that's what flats must reproduce once the scope is parked and
        /// sky angles no longer mean anything.</summary>
        private void RecordFlatSpec(TargetBlock block, string filterName, int gain, int offset, int binX, int binY) {
            try {
                float? mech = null;
                var rotInfo = _rotatorMediator.GetInfo();
                if (rotInfo?.Connected == true) mech = rotInfo.MechanicalPosition;

                bool added = false;
                List<FlatSpec> snapshot = null;
                lock (_flatSpecs) {
                    bool exists = _flatSpecs.Any(s =>
                        string.Equals(s.TargetName, block.TargetName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(s.FilterName, filterName, StringComparison.OrdinalIgnoreCase) &&
                        Math.Abs(s.RotationDeg - block.RotationDeg) < 0.05 &&
                        s.Gain == gain && s.Offset == offset && s.BinX == binX && s.BinY == binY);
                    if (!exists) {
                        var projectName = block.Profile?.Target?.ProjectName ?? "";
                        var cloudLedger = block.Profile?.Target?.Constraints?.Flats;
                        if (!string.IsNullOrEmpty(projectName) && cloudLedger != null)
                            lock (_cloudLedgers) _cloudLedgers[projectName] = cloudLedger;
                        _flatSpecs.Add(new FlatSpec {
                            TargetName = block.TargetName,
                            ProjectName = projectName,
                            FilterName = filterName,
                            RotationDeg = block.RotationDeg,
                            MechanicalRotation = mech,
                            Gain = gain,
                            Offset = offset,
                            BinX = binX,
                            BinY = binY,
                        });
                        snapshot = _flatSpecs.ToList();
                        added = true;
                    }
                }
                if (added) {
                    // A new combo after flats already ran means fresh lights without flats —
                    // re-arm the pass so the next session complete covers them. The store save
                    // below writes FlatsCompletedUtc = null, so disk and memory agree.
                    if (_flatsDone) {
                        _flatsDone = false;
                        global::NINA.Core.Utility.Logger.Info(
                            "AstroPM | Flats: new combo captured after flats completed — re-arming flat handling for tonight");
                    }
                    SaveFlatStore(snapshot, null);
                    UpdateFlatsSummary();
                    global::NINA.Core.Utility.Logger.Info(
                        $"AstroPM | Flats: recorded combo {block.TargetName} {filterName} G{gain} O{offset} {binX}×{binY} @ PA {block.RotationDeg:F1}°" +
                        (mech.HasValue ? $" (rotator mech {mech.Value:F1}°)" : " (no rotator)"));
                }
            } catch (Exception ex) {
                global::NINA.Core.Utility.Logger.Warning($"AstroPM | Flats combo tracking failed: {ex.Message}");
            }
        }

        /// <summary>Full Set of Flats: for every (target, rotation) group captured tonight, append
        /// a spec for each wheel filter the group is missing — camera settings (gain/offset/bin)
        /// and the mechanical rotation are inherited from the group's recorded lights, so the
        /// synthesized flats file under the same target with matching trained-flat identity.
        /// Recorded combos keep their capture order; synthesized ones follow in wheel order.</summary>
        private List<FlatSpec> ExpandSpecsToFullWheel(List<FlatSpec> specs) {
            try {
                var wheel = _profileService?.ActiveProfile?.FilterWheelSettings?.FilterWheelFilters;
                if (wheel == null || wheel.Count == 0) {
                    global::NINA.Core.Utility.Logger.Warning(
                        "AstroPM | Flats: Full Set requested but no filters are defined in the NINA profile — using tonight's captures only");
                    return specs;
                }
                var result = new List<FlatSpec>(specs);
                foreach (var g in specs.GroupBy(s => new { T = s.TargetName ?? "", Rot = Math.Round(s.RotationDeg, 1) })) {
                    var template = g.First();
                    foreach (var f in wheel) {
                        if (string.IsNullOrEmpty(f?.Name)) continue;
                        // Compare by the wheel filter each capture resolves to — the schedule's "R"/"G"
                        // land on the wheel's "Red"/"Green", and a literal compare added them twice.
                        if (g.Any(s => string.Equals(s.FilterName, f.Name, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(ResolveNinaFilter(s.FilterName)?.Name, f.Name, StringComparison.OrdinalIgnoreCase))) continue;
                        result.Add(new FlatSpec {
                            TargetName = template.TargetName,
                            ProjectName = template.ProjectName,
                            FilterName = f.Name,
                            RotationDeg = template.RotationDeg,
                            MechanicalRotation = template.MechanicalRotation,
                            Gain = template.Gain,
                            Offset = template.Offset,
                            BinX = template.BinX,
                            BinY = template.BinY,
                        });
                    }
                }
                if (result.Count > specs.Count)
                    global::NINA.Core.Utility.Logger.Info(
                        $"AstroPM | Flats: Full Set of Flats — expanded {specs.Count} captured combos to {result.Count} (all {wheel.Count} wheel filters at each target/rotation)");
                return result;
            } catch (Exception ex) {
                global::NINA.Core.Utility.Logger.Warning(
                    $"AstroPM | Flats: Full Set expansion failed ({ex.Message}) — using tonight's captures only");
                return specs;
            }
        }

        /// <summary>Per-run map from a deduped spec to the OTHER target names it stands in for —
        /// consumed by CopyFlatsToDuplicateTargets after each combo's pass.</summary>
        private Dictionary<FlatSpec, List<string>> _flatsCopyMap;

        /// <summary>Collapse specs that differ only by target — same rotation, filter, gain,
        /// offset, and binning produce byte-identical flats, so one capture serves them all.
        /// The first target keeps the capture; the rest are recorded in
        /// <paramref name="copyMap"/> for post-pass file copies.</summary>
        private List<FlatSpec> DedupeSpecsAcrossTargets(List<FlatSpec> specs, Dictionary<FlatSpec, List<string>> copyMap) {
            var result = new List<FlatSpec>();
            var index = new Dictionary<string, FlatSpec>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in specs) {
                // Key on the wheel filter the name resolves to: one target's schedule says "G" while a
                // Full Set synthesizes the wheel's "Green" for another — same glass, one capture.
                string filterKey = ResolveNinaFilter(s.FilterName)?.Name ?? s.FilterName;
                string key = $"{Math.Round(s.RotationDeg, 1)}|{filterKey}|{s.Gain}|{s.Offset}|{s.BinX}x{s.BinY}";
                if (index.TryGetValue(key, out var primary)) {
                    if (!string.IsNullOrEmpty(s.TargetName)
                        && !string.Equals(primary.TargetName, s.TargetName, StringComparison.OrdinalIgnoreCase)) {
                        if (!copyMap.TryGetValue(primary, out var list)) copyMap[primary] = list = new List<string>();
                        if (!list.Contains(s.TargetName, StringComparer.OrdinalIgnoreCase)) list.Add(s.TargetName);
                    }
                } else {
                    index[key] = s;
                    result.Add(s);
                }
            }
            if (result.Count < specs.Count)
                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Flats: {specs.Count} combos collapse to {result.Count} physical captures (identical rotation/filter/camera across targets); files will be copied to the duplicate targets");
            return result;
        }

        /// <summary>After a deduped combo's pass: copy the flat files NINA just saved into each
        /// duplicate target's folder by swapping the primary target's name inside the saved
        /// path. If the user's file pattern has no $$TARGETNAME$$, the paths contain no target
        /// segment and there is nothing to mirror — the single shared set is already right.</summary>
        private void CopyFlatsToDuplicateTargets(FlatSpec spec, List<string> savedPaths, List<string> dupTargets) {
            try {
                List<string> paths;
                lock (savedPaths) paths = savedPaths.ToList();
                string primary = spec.TargetName ?? "";
                if (paths.Count == 0) {
                    global::NINA.Core.Utility.Logger.Warning(
                        $"AstroPM | Flats: no saved flat files observed for {primary} {spec.FilterName} — nothing to copy to {dupTargets.Count} duplicate target(s)");
                    return;
                }
                if (string.IsNullOrEmpty(primary)) return;

                foreach (var dup in dupTargets) {
                    string safeDup = SanitizeForPath(dup);
                    int copied = 0, skipped = 0;
                    foreach (var src in paths) {
                        if (src.IndexOf(primary, StringComparison.OrdinalIgnoreCase) < 0) { skipped++; continue; }
                        string dst = src.Replace(primary, safeDup, StringComparison.OrdinalIgnoreCase);
                        if (string.Equals(dst, src, StringComparison.OrdinalIgnoreCase)) continue;
                        try {
                            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(dst));
                            if (!System.IO.File.Exists(dst)) { System.IO.File.Copy(src, dst); copied++; }
                        } catch (Exception ex) {
                            global::NINA.Core.Utility.Logger.Warning($"AstroPM | Flats: copy failed {src} → {dst}: {ex.Message}");
                        }
                    }
                    if (skipped == paths.Count)
                        global::NINA.Core.Utility.Logger.Info(
                            $"AstroPM | Flats: saved paths contain no target-name segment (no $$TARGETNAME$$ in file pattern?) — shared set stands, no copies for '{dup}'");
                    else
                        global::NINA.Core.Utility.Logger.Info(
                            $"AstroPM | Flats: copied {copied} flat(s) ({spec.FilterName} @ {spec.RotationDeg:F1}°) from '{primary}' to '{dup}'");
                }
            } catch (Exception ex) {
                global::NINA.Core.Utility.Logger.Warning($"AstroPM | Flats: duplicate-target copy failed: {ex.Message}");
            }
        }

        /// <summary>Target names become folder/file segments — mirror the invalid-char scrub the
        /// primary name went through when NINA built the source path.</summary>
        private static string SanitizeForPath(string name) {
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        private void UpdateFlatsSummary() {
            List<FlatSpec> specs;
            lock (_flatSpecs) specs = _flatSpecs.ToList();
            if (specs.Count == 0) {
                FlatsSummary = "No exposures captured yet tonight.";
                return;
            }
            var parts = specs
                .GroupBy(s => new { s.TargetName, Rot = Math.Round(s.RotationDeg, 1) })
                .OrderBy(g => g.Key.TargetName, StringComparer.OrdinalIgnoreCase)
                .Select(g => {
                    // Only spell out gain/bin when the same filter was shot with two
                    // different camera specs at this target+rotation — otherwise keep it short.
                    var labels = g.Select(s => {
                        bool dup = g.Count(o => string.Equals(o.FilterName, s.FilterName, StringComparison.OrdinalIgnoreCase)) > 1;
                        return dup ? $"{s.FilterName} (G{s.Gain} {s.BinX}×{s.BinY})" : s.FilterName;
                    });
                    return $"{g.Key.TargetName} @ {g.Key.Rot:F1}°: {string.Join(", ", labels)}";
                });
            FlatsSummary = $"Tonight: {string.Join("  ·  ", parts)}";
        }

        /// <summary>Detached parent for the flat runners while they execute. NINA's execution
        /// strategy evaluates triggers on the running container AND every ancestor up the parent
        /// chain before/after each instruction — parented into the live sequence, the user's
        /// sequence-level triggers (Restore Guiding, meridian flip, autofocus…) fire between
        /// flat instructions (field report 7/25: Restore Guiding restarted PHD2 mid-flats).
        /// This shim has no Parent, so the trigger walk dead-ends here and NO trigger can run
        /// during the flats pass. Loop CONDITIONS are unaffected: "Loop while safe" etc.
        /// interrupt through the parent container's condition watchdog and our cancellation
        /// token, not the trigger walk. Implements IDeepSkyObjectContainer so NINA's image
        /// saver still resolves $$TARGETNAME$$ (it walks the exposure's parents for the
        /// nearest target-bearing container).</summary>
        private sealed class FlatsIsolationContainer : SequentialContainer, IDeepSkyObjectContainer {
            private InputTarget _target;
            public InputTarget Target {
                get => _target;
                set { _target = value; RaisePropertyChanged(); }
            }
            public NighttimeData NighttimeData { get; set; }
        }

        private FlatsIsolationContainer _flatsShim;

        /// <summary>Nothing else stops the guider at session end — without this, PHD2 keeps
        /// guiding from the last block straight into the flats pass unless the user's Before
        /// Flats box happens to park the mount.</summary>
        private async Task StopGuidingForFlats(CancellationToken token) {
            try {
                if (_guiderMediator.GetInfo()?.Connected == true) {
                    global::NINA.Core.Utility.Logger.Info("AstroPM | Flats: stopping guiding for flats pass");
                    await _guiderMediator.StopGuiding(token);
                }
            } catch (OperationCanceledException) {
                throw;
            } catch (Exception ex) {
                global::NINA.Core.Utility.Logger.Warning(
                    $"AstroPM | Flats: stop guiding failed: {ex.Message} — continuing with flats");
            }
        }

        /// <summary>At session complete: for each rotation used tonight, move the rotator to its
        /// recorded mechanical position, then for each filter used at that rotation, switch the
        /// wheel and run the Flat Handling instructions once. _flatsDone is only set after a full
        /// successful pass, so a cancel/restart retries flats rather than silently skipping them.</summary>
        private async Task RunFlatsIfNeeded(IProgress<ApplicationStatus> progress, CancellationToken token) {
            if (_flatsDone) return;
            if (!FlatsEnabled) {
                global::NINA.Core.Utility.Logger.Info("AstroPM | Flats: disabled — skipping flat handling");
                return;
            }
            bool hasSetup = FlatsSetupRunner?.GetItemsSnapshot().Count > 0;
            bool hasPerCombo = FlatsRunner?.GetItemsSnapshot().Count > 0;
            bool hasTeardown = FlatsTeardownRunner?.GetItemsSnapshot().Count > 0;
            if (!hasSetup && !hasPerCombo && !hasTeardown) return;

            List<FlatSpec> specs;
            lock (_flatSpecs) specs = _flatSpecs.ToList();

            // Auto Flats Per Project: combos a missed pass left behind join tonight's.
            List<FlatSpec> carried;
            lock (_carryOverSpecs) carried = FlatsAutoPerProject ? _carryOverSpecs.ToList() : new List<FlatSpec>();
            if (carried.Count > 0) {
                int added = 0;
                foreach (var c in carried)
                    if (!specs.Any(s => SameCombo(s, c))) { specs.Add(c); added++; }
                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Flats: merged {added} carried-over combos from {_carryOverNight} into tonight's pass");
            }

            if (specs.Count == 0) {
                global::NINA.Core.Utility.Logger.Info("AstroPM | Flats: no captures recorded tonight — nothing to take flats for");
                _flatsDone = true;
                return;
            }

            // Full Set of Flats: at every target/rotation captured tonight, also run the
            // wheel filters that were NOT shot — one pass builds a complete flat library.
            if (FlatsFullSet) specs = ExpandSpecsToFullWheel(specs);

            // Auto Flats Per Project: keep only the combos whose project still needs flats
            // (per the cloud ledger the desktop pushed + what this plugin has taken itself).
            if (FlatsAutoPerProject) {
                specs = ApplyAutoFlatsPolicy(specs);
                if (specs.Count == 0) {
                    global::NINA.Core.Utility.Logger.Info("AstroPM | Flats: every project already has the flats it needs — skipping the pass");
                    Notification.ShowInformation("Astro PM: Flats skipped — all projects already have current flats.");
                    _flatsDone = true;
                    lock (_carryOverSpecs) _carryOverSpecs.Clear();
                    _carryOverNight = "";
                    SaveFlatStore(new List<FlatSpec>(), DateTime.UtcNow);
                    return;
                }
            }

            // Several targets sharing a rotation+filter+camera combo produce byte-identical
            // flats, so only ONE physical capture is needed — dedupe here, remember which
            // other targets each surviving spec stands in for, and copy the saved files to
            // them after each combo's pass (see CopyFlatsToDuplicateTargets). Mosaic panels
            // are the common case: every panel shares one rotation and filter set.
            _flatsCopyMap = new Dictionary<FlatSpec, List<string>>();
            specs = DedupeSpecsAcrossTargets(specs, _flatsCopyMap);

            // Guider down + trigger blackout for the whole pass (see FlatsIsolationContainer).
            await StopGuidingForFlats(token);
            _flatsShim = new FlatsIsolationContainer { Target = Target, NighttimeData = NighttimeData };
            try {
                await RunFlatsCore(specs, hasSetup, hasPerCombo, hasTeardown, progress, token);
            } finally {
                // Re-home the runners: a cancelled pass would otherwise leave them parented
                // to the shim, and the next pass/UI expects the normal tree.
                _flatsShim = null;
                _flatsCopyMap = null;
                FlatsSetupRunner?.AttachNewParent(this);
                FlatsRunner?.AttachNewParent(this);
                FlatsTeardownRunner?.AttachNewParent(this);
            }
        }

        private async Task RunFlatsCore(List<FlatSpec> specs, bool hasSetup, bool hasPerCombo,
            bool hasTeardown, IProgress<ApplicationStatus> progress, CancellationToken token) {
            bool useRotator = _rotatorMediator.GetInfo()?.Connected == true;
            global::NINA.Core.Utility.Logger.Info(
                $"AstroPM | Flats: starting — {specs.Count} filter/rotation combos, rotator {(useRotator ? "connected" : "not connected")}");

            // ── Before Flats: one-time setup (park mount, close flat panel, light on…) ──
            if (hasSetup) {
                LiveCommand = "Flats Setup";
                LiveTarget = "Flat Handling";
                HasLiveStatus = true;
                progress?.Report(new ApplicationStatus { Status = "Astro PM: Flats — running setup instructions..." });
                try {
                    global::NINA.Core.Utility.Logger.Info("AstroPM | Flats: running Before Flats instructions");
                    FlatsSetupRunner.AttachNewParent(_flatsShim);
                    ResetRunnerProgress(FlatsSetupRunner);
                    await FlatsSetupRunner.Run(progress, token);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    global::NINA.Core.Utility.Logger.Error(
                        $"AstroPM | Flats: Before Flats instructions failed: {ex.Message} — continuing with flats");
                }
            }

            FlatsRunner.AttachNewParent(_flatsShim);

            // Group by rotation so the rotator moves once per angle, preserving capture order
            // within each group (= the filter order used at night).
            var rotationGroups = hasPerCombo
                ? specs.GroupBy(s => Math.Round(s.RotationDeg, 1)).OrderBy(g => g.Key).ToList()
                : new List<IGrouping<double, FlatSpec>>();

            foreach (var group in rotationGroups) {
                token.ThrowIfCancellationRequested();

                var mech = group.Select(s => s.MechanicalRotation).FirstOrDefault(m => m.HasValue);
                if (useRotator && mech.HasValue) {
                    progress?.Report(new ApplicationStatus { Status = $"Astro PM: Rotating to {mech.Value:F1}° (mech) for flats..." });
                    LiveCommand = "Taking Flats";
                    LiveTarget = "Flat Handling";
                    LiveRotation = $"{group.Key:F1}°";
                    await MoveRotatorForFlatsAsync(mech.Value, group.Key, token);
                }

                // Within a rotation, run each target's combos under that target's name so NINA's
                // $$TARGETNAME$$ file-pattern token files the flats with the target's lights.
                foreach (var targetGroup in group.GroupBy(s => s.TargetName ?? "", StringComparer.OrdinalIgnoreCase)) {
                    if (!string.IsNullOrEmpty(targetGroup.Key)) SetFlatsTarget(targetGroup.Key);

                foreach (var spec in targetGroup) {
                    token.ThrowIfCancellationRequested();

                    LiveCommand = "Taking Flats";
                    LiveTarget = string.IsNullOrEmpty(spec.TargetName) ? "Flat Handling" : spec.TargetName;
                    LiveFilter = spec.FilterName;
                    LiveRotation = $"{group.Key:F1}°";
                    LiveGainOffset = $"Gain {spec.Gain} · Offset {spec.Offset} · Bin {spec.BinX}×{spec.BinY}";
                    HasLiveStatus = true;
                    progress?.Report(new ApplicationStatus {
                        Status = $"Astro PM: Flats — {spec.FilterName} G{spec.Gain} {spec.BinX}×{spec.BinY} @ {group.Key:F1}°..."
                    });

                    var filter = ResolveNinaFilter(spec.FilterName);
                    if (filter != null) {
                        await _filterWheelMediator.ChangeFilter(filter, token);
                    } else {
                        global::NINA.Core.Utility.Logger.Warning(
                            $"AstroPM | Flats: filter '{spec.FilterName}' not found in NINA filter wheel — running instructions anyway");
                    }

                    // Push this combo's filter + camera spec into every NINA flat instruction in
                    // the box (trained, auto-exposure, auto-brightness, sky), so each pass always
                    // matches the lights — no reliance on the user configuring each instruction.
                    ApplyComboToTrainedFlats(FlatsRunner, spec, filter);

                    // When this combo stands in for other targets, watch what NINA saves during
                    // its pass so the files can be mirrored into their folders after.
                    List<string> savedPaths = null;
                    List<string> dupTargets = null;
                    EventHandler<ImageSavedEventArgs> saveHandler = null;
                    if (_flatsCopyMap != null && _flatsCopyMap.TryGetValue(spec, out dupTargets) && dupTargets.Count > 0) {
                        savedPaths = new List<string>();
                        var sink = savedPaths;
                        saveHandler = (o, args) => {
                            try {
                                if (args?.PathToImage == null) return;
                                lock (sink) sink.Add(args.PathToImage.LocalPath);
                            } catch { }
                        };
                        _imageSaveMediator.ImageSaved += saveHandler;
                    }

                    bool comboOk = false;
                    try {
                        global::NINA.Core.Utility.Logger.Info(
                            $"AstroPM | Flats: running instructions for {spec.TargetName} {spec.FilterName} G{spec.Gain} O{spec.Offset} {spec.BinX}×{spec.BinY} @ {group.Key:F1}°");
                        // Reset child status to CREATED before each pass — we call Run() directly,
                        // which bypasses the framework's per-run reset (same as the trigger sets).
                        // Must be the deep variant: Trained Flat Exposure's nested loop counter
                        // survives a plain ResetProgress and it skips itself at 20/20.
                        ResetRunnerProgress(FlatsRunner);
                        await FlatsRunner.Run(progress, token);
                        comboOk = true;
                    } catch (OperationCanceledException) {
                        throw;
                    } catch (Exception ex) {
                        global::NINA.Core.Utility.Logger.Error(
                            $"AstroPM | Flats: instructions failed for {spec.TargetName} {spec.FilterName} @ {group.Key:F1}°: {ex.Message} — continuing with next combo");
                    } finally {
                        if (saveHandler != null) _imageSaveMediator.ImageSaved -= saveHandler;
                    }

                    if (savedPaths != null)
                        CopyFlatsToDuplicateTargets(spec, savedPaths, dupTargets);

                    // Ledger: this project (and every target the combo stood in for) now has
                    // flats for this combo as of now — the Auto Flats decision reads it next time.
                    if (comboOk) RecordFlatsTaken(spec, filter?.Name, dupTargets);
                }
                }
            }

            // ── After Flats: one-time teardown (light off, open panel…) ──
            if (hasTeardown) {
                LiveCommand = "Flats Teardown";
                LiveTarget = "Flat Handling";
                LiveFilter = "";
                progress?.Report(new ApplicationStatus { Status = "Astro PM: Flats — running teardown instructions..." });
                try {
                    global::NINA.Core.Utility.Logger.Info("AstroPM | Flats: running After Flats instructions");
                    FlatsTeardownRunner.AttachNewParent(_flatsShim);
                    ResetRunnerProgress(FlatsTeardownRunner);
                    await FlatsTeardownRunner.Run(progress, token);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    global::NINA.Core.Utility.Logger.Error(
                        $"AstroPM | Flats: After Flats instructions failed: {ex.Message}");
                }
            }

            _flatsDone = true;
            lock (_carryOverSpecs) _carryOverSpecs.Clear();   // made up — nothing left to carry
            _carryOverNight = "";
            SaveFlatStore(specs, DateTime.UtcNow);
            LiveCommand = "Session Complete";
            LiveTarget = "";
            LiveFilter = "";
            LiveRotation = "";
            global::NINA.Core.Utility.Logger.Info($"AstroPM | Flats: complete — {specs.Count} combos processed");
            Notification.ShowSuccess($"Astro PM: Flat handling complete — {specs.Count} filter/rotation combos");
        }

        /// <summary>Auto Flats Per Project filter: drop every combo whose project already has
        /// matching flats — in the desktop's cloud ledger (flats on disk in the project's local
        /// folders) or in this plugin's own ledger — per the mode: Once Per Project = any match;
        /// Time Based = a match newer than the interval. Filters are matched by the desktop
        /// name AND the NINA wheel name, rotation mechanical-to-mechanical within 2°.</summary>
        private List<FlatSpec> ApplyAutoFlatsPolicy(List<FlatSpec> specs) {
            var mode = FlatsAutoMode;
            int days = FlatsAutoIntervalDays;
            var local = FlatsLedgerStore.Load();
            var now = DateTime.UtcNow;
            var keep = new List<FlatSpec>();
            foreach (var spec in specs) {
                FlatsLedgerData cloud = null;
                if (!string.IsNullOrEmpty(spec.ProjectName))
                    lock (_cloudLedgers) _cloudLedgers.TryGetValue(spec.ProjectName, out cloud);
                string ninaName = null;
                try { ninaName = ResolveNinaFilter(spec.FilterName)?.Name; } catch { }
                bool any = FlatsAutoPolicy.FindCoverage(
                    spec.ProjectName, spec.FilterName, ninaName, spec.MechanicalRotation,
                    spec.Gain, spec.Offset, spec.BinX, cloud, local, out var newest, out var source);
                bool covered = FlatsAutoPolicy.IsCovered(mode, days, any, newest, now, out var why);
                var who = string.IsNullOrEmpty(spec.ProjectName) ? spec.TargetName : spec.ProjectName;
                var rot = spec.MechanicalRotation.HasValue ? $"{spec.MechanicalRotation.Value:F1}° mech" : "no rotator";
                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Flats/auto ({(mode == "TimeBased" ? $"every {days} d" : "once per project")}): {who} {spec.FilterName} G{spec.Gain} O{spec.Offset} {spec.BinX}×{spec.BinY} @ {rot} → {(covered ? "SKIP" : "TAKE")} — {why}{(any && !string.IsNullOrEmpty(source) ? $" [{source} ledger]" : "")}");
                if (!covered) keep.Add(spec);
            }
            global::NINA.Core.Utility.Logger.Info($"AstroPM | Flats/auto: {keep.Count} of {specs.Count} combos still need flats");
            return keep;
        }

        /// <summary>Write a completed combo into the plugin's flats ledger for its project and
        /// for every duplicate target it stood in for (their files were copied).</summary>
        private void RecordFlatsTaken(FlatSpec spec, string ninaFilterName, List<string> dupTargets) {
            try {
                var ledger = FlatsLedgerStore.Load();
                var now = DateTime.UtcNow;
                var filterName = string.IsNullOrWhiteSpace(ninaFilterName) ? spec.FilterName : ninaFilterName;
                double? mech = spec.MechanicalRotation;
                var projects = new List<string>();
                if (!string.IsNullOrEmpty(spec.ProjectName)) projects.Add(spec.ProjectName);
                if (dupTargets != null) {
                    foreach (var t in dupTargets) {
                        string p = null;
                        lock (_flatSpecs) p = _flatSpecs.FirstOrDefault(s => string.Equals(s.TargetName, t, StringComparison.OrdinalIgnoreCase))?.ProjectName;
                        if (string.IsNullOrEmpty(p)) lock (_carryOverSpecs) p = _carryOverSpecs.FirstOrDefault(s => string.Equals(s.TargetName, t, StringComparison.OrdinalIgnoreCase))?.ProjectName;
                        if (!string.IsNullOrEmpty(p) && !projects.Contains(p, StringComparer.OrdinalIgnoreCase)) projects.Add(p);
                    }
                }
                foreach (var p in projects)
                    ledger.Record(p, filterName, mech, spec.Gain, spec.Offset, spec.BinX, spec.BinY, now);
                ledger.Save();
            } catch (Exception ex) {
                global::NINA.Core.Utility.Logger.Warning($"AstroPM | Flats ledger write failed: {ex.Message}");
            }
        }

        /// <summary>ResetProgress alone only resets item STATUSES — loop-bearing instructions
        /// (e.g. Trained Flat Exposure's internal iteration loop) also keep a CompletedIterations
        /// counter on a nested container's CONDITION, which survives ResetProgress and makes the
        /// instruction skip itself on the next pass ("progress is already complete (20/20)" —
        /// field impact: night 2's flats in the same NINA session silently skip). Reset
        /// conditions recursively too.</summary>
        private static void ResetRunnerProgress(SequenceContainer runner) {
            if (runner == null) return;
            runner.ResetProgress();
            ResetConditionsRecursive(runner);
        }

        private static void ResetConditionsRecursive(ISequenceContainer container) {
            if (container is SequenceContainer sc && sc.Conditions != null) {
                foreach (var cond in sc.Conditions.ToList()) cond.ResetProgress();
            }
            foreach (var item in container.GetItemsSnapshot()) {
                if (item is ISequenceContainer child) ResetConditionsRecursive(child);
            }
        }

        /// <summary>Within this distance the rotator is already "there" for flats — no move is sent.
        /// A near-zero move is what hung a Wanderer rotator for 38 min on 10/4 (driver never reported
        /// the move finished); the light's mechanical position is stored to full precision, so the
        /// flats target is usually the rotator's current position anyway.</summary>
        private const double FlatsRotatorSkipToleranceDeg = 0.5;

        /// <summary>Upper bound on a flats rotator move. A full 180° swing on a slow rotator is well
        /// under this; past it the driver is assumed stuck and flats continue at the current angle.</summary>
        private static readonly TimeSpan FlatsRotatorMoveTimeout = TimeSpan.FromMinutes(2);

        /// <summary>Move the rotator to a combo's mechanical angle for flats, guarded against drivers
        /// that never report completion: skips moves already within tolerance, and gives up (halting
        /// the rotator) after <see cref="FlatsRotatorMoveTimeout"/> rather than blocking dawn flats.</summary>
        private async Task MoveRotatorForFlatsAsync(float targetMech, double pa, CancellationToken token) {
            var current = _rotatorMediator.GetInfo()?.MechanicalPosition;
            if (current.HasValue && !float.IsNaN(current.Value)) {
                double diff = Math.Abs(current.Value - targetMech) % 360.0;
                if (diff > 180.0) diff = 360.0 - diff;
                if (diff <= FlatsRotatorSkipToleranceDeg) {
                    global::NINA.Core.Utility.Logger.Info(
                        $"AstroPM | Flats: rotator already at mechanical {current.Value:F2}° (target {targetMech:F2}°, PA {pa:F1}°) — no move needed");
                    return;
                }
            }

            global::NINA.Core.Utility.Logger.Info(
                $"AstroPM | Flats: rotator → mechanical {targetMech:F2}° from {current?.ToString("F2") ?? "?"}° (PA {pa:F1}°)");

            using (var moveCts = CancellationTokenSource.CreateLinkedTokenSource(token)) {
                // NINA halts the rotator when this token cancels. The move is raced against a delay
                // rather than relying on that cancellation alone, because a driver blocked inside its
                // own Move call would never observe it.
                var moveTask = _rotatorMediator.MoveMechanical(targetMech, moveCts.Token);
                var finished = await Task.WhenAny(moveTask, Task.Delay(FlatsRotatorMoveTimeout, token));
                token.ThrowIfCancellationRequested();
                if (finished == moveTask) {
                    await moveTask;
                    return;
                }

                moveCts.Cancel();
                _ = moveTask.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                var now = _rotatorMediator.GetInfo()?.MechanicalPosition;
                global::NINA.Core.Utility.Logger.Warning(
                    $"AstroPM | Flats: rotator move to {targetMech:F2}° did not complete within {FlatsRotatorMoveTimeout.TotalMinutes:F0} min " +
                    $"(now {now?.ToString("F2") ?? "?"}°) — rotator driver did not report the move finished; continuing flats at the current angle");
                Notification.ShowWarning(
                    $"Astro PM: rotator didn't finish moving to {targetMech:F1}° for flats — continuing at the current angle. Try reconnecting the rotator.");
            }
        }

        /// <summary>Point the container's Target at the combo's originating target while its flats
        /// run. NINA's image saver walks the parent chain to this IDeepSkyObjectContainer, so the
        /// $$TARGETNAME$$ file-pattern token resolves to the same name the lights were saved under —
        /// flats land in the target's folder tree (under FLAT via $$IMAGETYPE$$). Coordinates are
        /// left empty: flats are taken parked/covered, so pointing metadata is meaningless.</summary>
        private void SetFlatsTarget(string targetName) {
            var astro = _profileService.ActiveProfile.AstrometrySettings;
            var target = new InputTarget(
                Angle.ByDegree(astro.Latitude),
                Angle.ByDegree(astro.Longitude),
                astro.Horizon);
            target.TargetName = targetName;
            if (target.DeepSkyObject != null) target.DeepSkyObject.Name = targetName;
            Target = target;
            // The runners hang off the isolation shim during flats, so the image saver's
            // parent walk finds the SHIM's target — keep it in sync with ours.
            if (_flatsShim != null) _flatsShim.Target = target;
            global::NINA.Core.Utility.Logger.Info($"AstroPM | Flats: saving under target '{targetName}'");
        }

        /// <summary>Recursively writes the combo's filter + camera spec into every NINA flat
        /// instruction in the box — Trained Flat/Dark Flat Exposure, Auto Exposure Flat, Auto
        /// Brightness Flat, and Sky Flat all expose the same embedded Switch Filter + Take
        /// Exposure items — so each shoots the combo that matches the lights exactly regardless
        /// of how the instruction was configured. Trained types additionally get their table
        /// lookup pre-checked so a missing row warns instead of dying in NINA's Execute.</summary>
        private void ApplyComboToTrainedFlats(ISequenceContainer container, FlatSpec spec, FilterInfo filter) {
            foreach (var item in container.GetItemsSnapshot()) {
                global::NINA.Sequencer.SequenceItem.FilterWheel.SwitchFilter switchFilter;
                global::NINA.Sequencer.SequenceItem.Imaging.TakeExposure exposure;
                bool usesTrainedTable;
                string kind;
                switch (item) {
                    case global::NINA.Sequencer.SequenceItem.FlatDevice.TrainedFlatExposure tfe:
                        switchFilter = tfe.GetSwitchFilterItem(); exposure = tfe.GetExposureItem();
                        usesTrainedTable = true; kind = "Trained Flat Exposure"; break;
                    case global::NINA.Sequencer.SequenceItem.FlatDevice.TrainedDarkFlatExposure tdfe:
                        switchFilter = tdfe.GetSwitchFilterItem(); exposure = tdfe.GetExposureItem();
                        usesTrainedTable = true; kind = "Trained Dark Flat Exposure"; break;
                    case global::NINA.Sequencer.SequenceItem.FlatDevice.AutoExposureFlat aef:
                        switchFilter = aef.GetSwitchFilterItem(); exposure = aef.GetExposureItem();
                        usesTrainedTable = false; kind = "Auto Exposure Flat"; break;
                    case global::NINA.Sequencer.SequenceItem.FlatDevice.AutoBrightnessFlat abf:
                        switchFilter = abf.GetSwitchFilterItem(); exposure = abf.GetExposureItem();
                        usesTrainedTable = false; kind = "Auto Brightness Flat"; break;
                    case global::NINA.Sequencer.SequenceItem.FlatDevice.SkyFlat sf:
                        switchFilter = sf.GetSwitchFilterItem(); exposure = sf.GetExposureItem();
                        usesTrainedTable = false; kind = "Sky Flat"; break;
                    default:
                        if (item is ISequenceContainer sub) ApplyComboToTrainedFlats(sub, spec, filter);
                        continue;
                }
                try {
                    if (switchFilter != null && filter != null) switchFilter.Filter = filter;
                    if (exposure != null) {
                        exposure.Gain = spec.Gain;
                        exposure.Offset = spec.Offset;
                        exposure.Binning = new BinningMode((short)spec.BinX, (short)spec.BinY);
                    }
                    if (usesTrainedTable) {
                        // NINA's trained flat/dark-flat Execute dereferences the trained-table row
                        // without a null check, so a combo the user never trained dies as a bare
                        // NullReferenceException. Pre-check the same lookup and say what's missing.
                        var trained = _profileService.ActiveProfile.FlatDeviceSettings.GetTrainedFlatExposureSetting(
                            filter?.Position, new BinningMode((short)spec.BinX, (short)spec.BinY), spec.Gain, spec.Offset);
                        if (trained == null) {
                            var comboDesc = $"{filter?.Name ?? spec.FilterName} {spec.BinX}×{spec.BinY} G{spec.Gain} O{spec.Offset}";
                            global::NINA.Core.Utility.Logger.Warning(
                                $"AstroPM | Flats: no trained flat entry for {comboDesc} — add it in NINA Equipment > Flat Panel (the {kind} for this combo will fail)");
                            Notification.ShowWarning($"Astro PM: No trained flat for {comboDesc} — train it in Equipment > Flat Panel");
                        }
                    }
                    global::NINA.Core.Utility.Logger.Info(
                        $"AstroPM | Flats: {kind} set to {filter?.Name ?? spec.FilterName} G{spec.Gain} O{spec.Offset} {spec.BinX}×{spec.BinY}");
                } catch (Exception ex) {
                    global::NINA.Core.Utility.Logger.Warning(
                        $"AstroPM | Flats: could not apply combo to {kind}: {ex.Message}");
                }
            }
        }

        /// <summary>Same three-tier filter matching as the exposure item: exact, then either
        /// name is a prefix of the other (handles "Ha" vs "Ha 3nm" style mismatches).</summary>
        private FilterInfo ResolveNinaFilter(string filterName) {
            var ninaFilters = _profileService.ActiveProfile.FilterWheelSettings.FilterWheelFilters;
            return ninaFilters?.FirstOrDefault(f => string.Equals(f.Name, filterName, StringComparison.OrdinalIgnoreCase))
                ?? ninaFilters?.FirstOrDefault(f => f.Name != null && f.Name.StartsWith(filterName, StringComparison.OrdinalIgnoreCase))
                ?? ninaFilters?.FirstOrDefault(f => f.Name != null && filterName.StartsWith(f.Name, StringComparison.OrdinalIgnoreCase));
        }

        private static (string Filter, double ExposureSec, int Gain, int Offset, int BinX, int BinY, int ReadoutMode) ParseExposureEntry(SimLogEntry entry) {
            string filter = entry.Filter ?? "—";
            double expSec = 300;
            if (!string.IsNullOrEmpty(entry.Exposure) && double.TryParse(entry.Exposure.TrimEnd('s'), out var parsed))
                expSec = parsed;
            int.TryParse(entry.Gain, out int gain);
            int.TryParse(entry.Offset, out int offset);

            int binX = 1, binY = 1;
            if (!string.IsNullOrEmpty(entry.Bin)) {
                if (entry.Bin.Contains("×")) {
                    var parts = entry.Bin.Split('×');
                    int.TryParse(parts[0], out binX);
                    int.TryParse(parts[1], out binY);
                } else {
                    int.TryParse(entry.Bin, out binX);
                    binY = binX;
                }
            }

            int.TryParse(entry.ReadoutMode, out int readoutIdx);
            return (filter, expSec, gain, offset, binX, binY, readoutIdx);
        }

        private static List<TargetBlock> ParseBlocks(List<SimLogEntry> log, List<TargetProfile> profiles) {
            var blocks = new List<TargetBlock>();
            TargetBlock current = null;

            foreach (var entry in log) {
                if (entry.Command == "Slew") {
                    if (current != null) {
                        current.UtcEnd = entry.UtcTime;
                        blocks.Add(current);
                    }

                    bool isPanelSlew = entry.Target.Contains(" → ");
                    var slewTarget = isPanelSlew
                        ? entry.Target.Substring(0, entry.Target.IndexOf(" → "))
                        : entry.Target;
                    var panelLabel = isPanelSlew
                        ? entry.Target.Substring(entry.Target.IndexOf(" → ") + 3)
                        : "";

                    var profile = profiles.FirstOrDefault(p => p.DisplayName == slewTarget)
                               ?? profiles.FirstOrDefault(p => p.Target.TargetName == slewTarget);

                    double ra = profile?.Target.RaHours ?? 0;
                    double dec = profile?.Target.DecDegrees ?? 0;
                    double rot = profile?.Target.RotationDeg ?? 0;

                    if (profile != null && profile.PanelIndex.HasValue) {
                        var pnl = profile.Target.Panels?.FirstOrDefault(p => p.PanelIndex == profile.PanelIndex.Value);
                        if (pnl != null) {
                            ra = pnl.RaHours;
                            dec = pnl.DecDegrees;
                            rot = pnl.RotationDeg;
                        }
                    } else if (isPanelSlew && profile != null) {
                        // Engine panel labels are POSITIONAL — "P1" = first panel in
                        // PanelIndex order. They never equal the cloud's Label text
                        // ("Panel 1"), so matching on Label always fell back to the
                        // project-center coordinates and every panel slewed to the middle
                        // of the mosaic.
                        var pnl = ResolvePanelByEngineLabel(profile, panelLabel);
                        if (pnl != null) {
                            ra = pnl.RaHours;
                            dec = pnl.DecDegrees;
                            rot = pnl.RotationDeg;
                        }
                    }

                    var displayName = isPanelSlew ? $"{slewTarget} {panelLabel}" : slewTarget;

                    current = new TargetBlock {
                        TargetName = displayName,
                        RaHours = ra,
                        DecDegrees = dec,
                        RotationDeg = rot,
                        UtcStart = entry.UtcTime,
                        Profile = profile,
                    };
                }

                if (entry.Command == "Wait") {
                    if (current != null) {
                        current.UtcEnd = entry.UtcTime;
                        blocks.Add(current);
                        current = null;
                    }
                }

                if (current != null && entry.UtcTime != default) {
                    current.Entries.Add(entry);
                }
            }

            if (current != null) {
                var endEntry = log.LastOrDefault(e => e.Command == "End");
                current.UtcEnd = endEntry?.UtcTime ?? current.UtcStart.AddHours(1);
                blocks.Add(current);
            }

            // Mosaic fixup. The engine's log is panel-accurate (every Image entry carries
            // its Panel label), but the block boundaries hide it in two spots: the INITIAL
            // slew to a mosaic is logged with the bare target name (the panel is only
            // picked after the slew), so the first block filed under a suffix-less name
            // and slewed to the project center. Derive each block's panel from its own
            // exposure entries, then re-point the block at that panel.
            foreach (var b in blocks) {
                var panels = b.Profile?.Target?.Panels;
                if (panels == null || panels.Count <= 1) continue;
                if (b.Profile.PanelIndex.HasValue) continue;   // per-panel profiles are already exact
                string pl = b.Entries.FirstOrDefault(e => !string.IsNullOrEmpty(e.Panel))?.Panel;
                if (string.IsNullOrEmpty(pl)) continue;
                if (!b.TargetName.EndsWith(" " + pl, StringComparison.OrdinalIgnoreCase))
                    b.TargetName = $"{b.TargetName} {pl}";
                var pnl = ResolvePanelByEngineLabel(b.Profile, pl);
                if (pnl != null) {
                    b.RaHours = pnl.RaHours;
                    b.DecDegrees = pnl.DecDegrees;
                    b.RotationDeg = pnl.RotationDeg;
                } else {
                    // Never fall back silently — a mosaic block pointed at the project
                    // center instead of its panel is how panels imaged the same field
                    // for weeks without anyone noticing (8/28/26).
                    global::NINA.Core.Utility.Logger.Warning(
                        $"AstroPM | Mosaic block '{b.TargetName}': panel '{pl}' did not resolve to a panel " +
                        $"({panels.Count} panels on target) — block keeps target-level coordinates. Check panel data.");
                }
            }

            return blocks;
        }

        /// <summary>Maps an engine panel label ("P1", "P2"…) to its PanelData. Engine labels
        /// are positional — "P{n}" is the n-th panel in PanelIndex order (see
        /// SessionScheduler.PickExposureSet) — NOT the cloud's display Label ("Panel 1").</summary>
        private static PanelData ResolvePanelByEngineLabel(TargetProfile profile, string panelLabel) {
            var panels = profile?.Target?.Panels;
            if (panels == null || string.IsNullOrEmpty(panelLabel)) return null;
            if (panelLabel.Length < 2 || panelLabel[0] != 'P') return null;
            if (!int.TryParse(panelLabel.Substring(1), out int n)) return null;
            var ordered = panels.OrderBy(p => p.PanelIndex).ToList();
            return n >= 1 && n <= ordered.Count ? ordered[n - 1] : null;
        }

        private static List<SortCriteria> ParseSortChain(string csv) {
            if (string.IsNullOrWhiteSpace(csv))
                return new List<SortCriteria>(ScheduleEngine.DefaultSortChain);
            var parsed = new List<SortCriteria>();
            foreach (var v in csv.Split(',')) {
                if (Enum.TryParse<SortCriteria>(v.Trim(), out var sc))
                    parsed.Add(sc);
            }
            return parsed.Count > 0 ? parsed : new List<SortCriteria>(ScheduleEngine.DefaultSortChain);
        }

        private static string FormatLogLine(SimLogEntry e) {
            var parts = new List<string> { e.Command.PadRight(7), e.Time.PadRight(6) };
            if (!string.IsNullOrEmpty(e.Target)) parts.Add(e.Target);
            if (!string.IsNullOrEmpty(e.Panel)) parts.Add(e.Panel);
            if (!string.IsNullOrEmpty(e.SubNum)) parts.Add(e.SubNum);
            if (!string.IsNullOrEmpty(e.Filter)) parts.Add(e.Filter);
            if (!string.IsNullOrEmpty(e.Exposure)) parts.Add(e.Exposure);
            if (!string.IsNullOrEmpty(e.Gain)) parts.Add($"G{e.Gain}");
            if (!string.IsNullOrEmpty(e.Offset)) parts.Add($"O{e.Offset}");
            if (!string.IsNullOrEmpty(e.Bin)) parts.Add($"Bin{e.Bin}");
            return string.Join("  ", parts);
        }

        public override object Clone() {
            return new TargetInstructionSet(this);
        }

        public override string ToString() {
            return $"Category: Astro PM Tools, Item: {nameof(TargetInstructionSet)}";
        }
    }
}
