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
    [ExportMetadata("Icon", "ParallelSVG")]
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
        private List<ProjectTarget> _cachedTargets;
        private bool _hasChartData;
        private bool _scheduleBuilt;
        private DateTime _sessionEndUtc;

        // Persisted across interruptions
        private int _currentBlockIndex;

        // ── Flat Handling ──
        // (filter, rotation) combos captured tonight; the Flat Handling box iterates these
        // at session complete. Guarded by lock(_flatSpecs) — captures record from the
        // execution loop while the UI reads the summary.
        private readonly List<FlatSpec> _flatSpecs = new List<FlatSpec>();
        private string _nightDate = "";
        private bool _flatsDone;

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
            AstroPMSettings.ExternallyChanged += () => RaisePropertyChanged(nameof(FlatsEnabled));

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

        [OnSerializing]
        private void OnSerializingStripRuntimeItems(StreamingContext context) {
            ScrubPlaceholders();
        }

        [OnSerialized]
        private void OnSerializedRestorePlaceholder(StreamingContext context) {
            EnsurePlaceholder();
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
            new FlatSpecStore().Save();
            UpdateFlatsSummary();
            // Also clear the checkmarks/progress the last flats pass left on the drop-zone
            // instructions — including nested loop counters (see ResetRunnerProgress).
            ResetRunnerProgress(FlatsSetupRunner);
            ResetRunnerProgress(FlatsRunner);
            ResetRunnerProgress(FlatsTeardownRunner);
            global::NINA.Core.Utility.Logger.Info("AstroPM | Manual reset — schedule and flat tracking cleared, will re-fetch on next run");
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
                lock (_flatSpecs) return _flatSpecs.Count > 0;
            }
        }

        /// <summary>True when the built schedule is left over from a finished night. A
        /// session goes stale StaleAfterHours after its end time: long enough that the
        /// dawn wind-down (watchdog condition checks, flats, the daily loop's dawn tasks)
        /// still sees it as tonight's and the nightly loop exits cleanly, short enough
        /// that a sequence started any time later that day — morning included — resets
        /// and rebuilds for the coming night instead of being skipped.</summary>
        public bool IsStaleSession {
            get {
                if (!_scheduleBuilt || _sessionEndUtc == DateTime.MinValue) return false;
                return DateTime.UtcNow >= _sessionEndUtc.AddHours(StaleAfterHours);
            }
        }

        private const double StaleAfterHours = 2;

        public void ResetForNewNight() {
            _scheduleBuilt = false;
            _blocks = null;
            _currentBlockIndex = 0;
            _sessionEndUtc = DateTime.MinValue;
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

            // A schedule left over from a finished night (per IsStaleSession) must be
            // discarded — the in-memory state survives sequence stop/restart, so
            // without this reset a re-run instantly reports "session complete"
            // instead of building tonight's schedule.
            // FlatsPending exception: right at session end the flats pass hasn't run yet
            // (and a stop/restart during flats re-enters here) — resetting would nuke the
            // blocks and rebuild mid-flats. Skip the reset until flats complete.
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

            try {
                if (needsBuild) {
                    var reason = !_scheduleBuilt ? "first run or reset" : "no blocks";
                    global::NINA.Core.Utility.Logger.Info($"AstroPM | Execute: building schedule ({reason})");
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
                        global::NINA.Core.Utility.Logger.Info(
                            $"AstroPM | Schedule build produced no blocks — eligible to rebuild after {StaleAfterHours}h");
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
                // Sequence was stopped — reset status displays
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
                        TargetCacheService.Save(targets);
                        fetchSourceDesc = $"fetched from cloud {DateTime.Now:MMM d, h:mm tt}";
                        Logger.Info($"AstroPM | Fetched {targets.Count} targets from cloud");
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

            // Keep the full loaded list so offline capture-tracking persists count updates
            // without dropping unscheduled targets from the cache.
            _cachedTargets = targets;

            targets = targets.Where(t => t.Panels != null && t.Panels.Any(p =>
                p.ExposureSets != null && p.ExposureSets.Any(es => es.Remaining > 0))).ToList();

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
            var date = localNow.Hour < 12 ? DateTime.Today.AddDays(-1) : DateTime.Today;
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

            var profiles = SessionScheduler.BuildTargetProfiles(targets, slots, latDeg, lonDeg, settings.MosaicPanelPreference, customHorizon);

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
            var matrix = ScheduleEngine.BuildMatrix(slots, profiles, order);

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

            // Check constraints — bypass viability when the wait was skipped (e.g. Skip Block)
            if (!_skipWait && !IsTargetViable(block, latDeg, lonDeg)) {
                global::NINA.Core.Utility.Logger.Info($"AstroPM | Target constraints failed, skipping: {block.TargetName}");
                if (_blockSummaries != null && _currentBlockIndex < _blockSummaries.Count)
                    _blockSummaries[_currentBlockIndex].Status = "skipped";
                _currentBlockIndex++;
                return;
            }

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
            if (_skipBlock) { FinishBlock(block, skipped: true); return; }

            // ── Fire "Before Target" triggers ──
            // Fires with the scope already slewed/centered/rotated on the new target, before
            // guiding and imaging start — so dropped instructions (autofocus, settle waits,
            // covers, etc.) run on-target rather than while still pointed at the old one.
            var parentForTargetTriggers = Parent as SequenceContainer;
            if (parentForTargetTriggers != null) {
                foreach (var trigger in parentForTargetTriggers.GetTriggersSnapshot()) {
                    if (trigger is AstroPMBeforeTargetTrigger beforeTarget)
                        await beforeTarget.Fire(block, progress, token);
                }
            }

            token.ThrowIfCancellationRequested();
            if (_skipBlock) { FinishBlock(block, skipped: true); return; }

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
            bool offlineMode = AstroPMSettings.Load().OfflineMode;
            global::NINA.Core.Utility.Logger.Info(
                $"AstroPM | Block {block.TargetName}: playback mode = {(sequential ? "Sequential" : "Time-Aware")}");

            while (DateTime.UtcNow < block.UtcEnd && !_skipBlock) {
                token.ThrowIfCancellationRequested();

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
                        if (offlineMode)
                            RecordOfflineCapture(block, entry.Panel, filterName, exposureSec, gain, offset, binX, binY);
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
                if (parentContainer != null) {
                    try {
                        UpdateLiveStatus("Triggers", block);
                        global::NINA.Core.Utility.Logger.Info(
                            $"AstroPM | Running pre-triggers: {block.TargetName} {filterName} #{targetIndex + 1}");
                        await parentContainer.RunTriggers(previousExposure ?? this, exposureItem, progress, token);
                    } catch (Exception ex) {
                        global::NINA.Core.Utility.Logger.Warning(
                            $"AstroPM | Trigger error before exposure {block.TargetName} #{targetIndex + 1}: {ex.GetType().Name}: {ex.Message}");
                    }
                }

                // Take the exposure
                await exposureItem.Execute(progress, token);

                // Fire post-triggers
                if (parentContainer != null) {
                    try {
                        await parentContainer.RunTriggersAfter(exposureItem, this, progress, token);
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

            // ── Fire "After Target" triggers ──
            if (parentContainer != null) {
                foreach (var trigger in parentContainer.GetTriggersSnapshot()) {
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

        private bool IsTargetViable(TargetBlock block, double latDeg, double lonDeg) {
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

            if (!isDark || !aboveMinAlt)
                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Viability failed: {block.TargetName} — alt={altitude:F1}° (min {c.MinTargetAltitude}°), sun={sunAlt:F1}° (max {c.SunAltitudeThreshold}°)");

            return isDark && aboveMinAlt;
        }

        /// <summary>Offline/Vacation Mode: count a freshly-captured LIGHT frame against the cached target so
        /// the next night's sim sees reduced remaining. Matches the exposure set by panel + filter + exposure
        /// (disambiguated by gain/offset/bin), increments Accepted/Acquired, and re-saves the cache.</summary>
        private void RecordOfflineCapture(TargetBlock block, string panelLabel, string filter, double exposureSec, int gain, int offset, int binX, int binY) {
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
                es.AcceptedCount++;   // offline: optimistically count toward Remaining (Planned - Accepted)

                if (_cachedTargets != null)
                    TargetCacheService.Save(_cachedTargets);

                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM | Offline cache updated: {target.TargetName}/{panel.Label} {filter} {exposureSec:F0}s → {es.AcceptedCount}/{es.PlannedCount} (remaining {es.Remaining})");
            } catch (Exception ex) {
                global::NINA.Core.Utility.Logger.Warning($"AstroPM | Offline capture tracking failed: {ex.Message}");
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
                        _flatSpecs.Add(new FlatSpec {
                            TargetName = block.TargetName,
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
                    new FlatSpecStore { NightDate = _nightDate, Specs = snapshot }.Save();
                    UpdateFlatsSummary();
                    global::NINA.Core.Utility.Logger.Info(
                        $"AstroPM | Flats: recorded combo {block.TargetName} {filterName} G{gain} O{offset} {binX}×{binY} @ PA {block.RotationDeg:F1}°" +
                        (mech.HasValue ? $" (rotator mech {mech.Value:F1}°)" : " (no rotator)"));
                }
            } catch (Exception ex) {
                global::NINA.Core.Utility.Logger.Warning($"AstroPM | Flats combo tracking failed: {ex.Message}");
            }
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
            if (specs.Count == 0) {
                global::NINA.Core.Utility.Logger.Info("AstroPM | Flats: no captures recorded tonight — nothing to take flats for");
                _flatsDone = true;
                return;
            }

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
                    FlatsSetupRunner.AttachNewParent(this);
                    ResetRunnerProgress(FlatsSetupRunner);
                    await FlatsSetupRunner.Run(progress, token);
                } catch (OperationCanceledException) {
                    throw;
                } catch (Exception ex) {
                    global::NINA.Core.Utility.Logger.Error(
                        $"AstroPM | Flats: Before Flats instructions failed: {ex.Message} — continuing with flats");
                }
            }

            FlatsRunner.AttachNewParent(this);

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
                    global::NINA.Core.Utility.Logger.Info(
                        $"AstroPM | Flats: rotator → mechanical {mech.Value:F1}° (PA {group.Key:F1}°)");
                    await _rotatorMediator.MoveMechanical(mech.Value, token);
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

                    try {
                        global::NINA.Core.Utility.Logger.Info(
                            $"AstroPM | Flats: running instructions for {spec.TargetName} {spec.FilterName} G{spec.Gain} O{spec.Offset} {spec.BinX}×{spec.BinY} @ {group.Key:F1}°");
                        // Reset child status to CREATED before each pass — we call Run() directly,
                        // which bypasses the framework's per-run reset (same as the trigger sets).
                        // Must be the deep variant: Trained Flat Exposure's nested loop counter
                        // survives a plain ResetProgress and it skips itself at 20/20.
                        ResetRunnerProgress(FlatsRunner);
                        await FlatsRunner.Run(progress, token);
                    } catch (OperationCanceledException) {
                        throw;
                    } catch (Exception ex) {
                        global::NINA.Core.Utility.Logger.Error(
                            $"AstroPM | Flats: instructions failed for {spec.TargetName} {spec.FilterName} @ {group.Key:F1}°: {ex.Message} — continuing with next combo");
                    }
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
                    FlatsTeardownRunner.AttachNewParent(this);
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
            new FlatSpecStore { NightDate = _nightDate, Specs = specs, FlatsCompletedUtc = DateTime.UtcNow }.Save();
            LiveCommand = "Session Complete";
            LiveTarget = "";
            LiveFilter = "";
            LiveRotation = "";
            global::NINA.Core.Utility.Logger.Info($"AstroPM | Flats: complete — {specs.Count} combos processed");
            Notification.ShowSuccess($"Astro PM: Flat handling complete — {specs.Count} filter/rotation combos");
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
                        var pnl = profile.Target.Panels?.FirstOrDefault(p =>
                            string.Equals(p.Label, panelLabel, StringComparison.OrdinalIgnoreCase));
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

            return blocks;
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
