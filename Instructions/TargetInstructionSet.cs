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
        private readonly IImageSaveMediator _imageSaveMediator;
        private readonly IImageHistoryVM _imageHistoryVM;
        private readonly IGuiderMediator _guiderMediator;
        private readonly IRotatorMediator _rotatorMediator;
        private readonly IDomeMediator _domeMediator;
        private readonly IDomeFollower _domeFollower;
        private readonly IPlateSolverFactory _plateSolverFactory;
        private readonly IWindowServiceFactory _windowServiceFactory;

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

        // Persisted across interruptions
        private int _currentBlockIndex;

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
            IImageSaveMediator imageSaveMediator,
            IImageHistoryVM imageHistoryVM,
            IGuiderMediator guiderMediator,
            IRotatorMediator rotatorMediator,
            IDomeMediator domeMediator,
            IDomeFollower domeFollower,
            IPlateSolverFactory plateSolverFactory,
            IWindowServiceFactory windowServiceFactory,
            INighttimeCalculator nighttimeCalculator) : base(new SequentialStrategy()) {
            _profileService = profileService;
            _telescopeMediator = telescopeMediator;
            _filterWheelMediator = filterWheelMediator;
            _imagingMediator = imagingMediator;
            _imageSaveMediator = imageSaveMediator;
            _imageHistoryVM = imageHistoryVM;
            _guiderMediator = guiderMediator;
            _rotatorMediator = rotatorMediator;
            _domeMediator = domeMediator;
            _domeFollower = domeFollower;
            _plateSolverFactory = plateSolverFactory;
            _windowServiceFactory = windowServiceFactory;
            _nighttimeCalculator = nighttimeCalculator;

            NighttimeData = nighttimeCalculator.Calculate();

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
            cloneMe._imagingMediator, cloneMe._imageSaveMediator, cloneMe._imageHistoryVM,
            cloneMe._guiderMediator, cloneMe._rotatorMediator,
            cloneMe._domeMediator, cloneMe._domeFollower,
            cloneMe._plateSolverFactory, cloneMe._windowServiceFactory,
            cloneMe._nighttimeCalculator) {
            CopyMetaData(cloneMe);
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
            global::NINA.Core.Utility.Logger.Info("AstroPM | Manual reset — schedule cleared, will re-fetch on next run");
            Notification.ShowInformation("Astro PM: Schedule reset. Start the sequence to fetch new targets.");
        });

        public System.Windows.Input.ICommand StartNowCommand => new RelayCommand(async _ => {
            _skipWait = true;
            global::NINA.Core.Utility.Logger.Info("AstroPM | Start Now pressed — skipping wait and viability check");
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
                if (_blocks == null || _blocks.Count == 0) return false;
                var now = DateTime.UtcNow;
                if (now >= _sessionEndUtc) return false;
                for (int i = _currentBlockIndex; i < _blocks.Count; i++) {
                    if (_blocks[i].UtcEnd > now) return true;
                }
                return false;
            }
        }

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

            // Only rebuild the schedule if we don't have an active session.
            // After a block completes, the loop calls Execute() again — we must NOT
            // rebuild because DateTime.Today flips after midnight, which would
            // reschedule remaining blocks for tomorrow night instead of continuing tonight.
            bool needsBuild = !_scheduleBuilt
                || _blocks == null || _blocks.Count == 0;

            try {
                if (needsBuild) {
                    var reason = !_scheduleBuilt ? "first run or reset" : "no blocks";
                    global::NINA.Core.Utility.Logger.Info($"AstroPM | Execute: building schedule ({reason})");
                    await BuildSchedule(progress, token);
                    _scheduleBuilt = true;
                    if (_blocks == null || _blocks.Count == 0) return;
                } else {
                    global::NINA.Core.Utility.Logger.Info(
                        $"AstroPM | Execute: continuing active session — block {_currentBlockIndex + 1}/{_blocks.Count}, session ends {_sessionEndUtc:HH:mm} UTC");
                }

                await ExecuteNextBlock(progress, token);

                // After the block finishes, show "Session Complete" if no blocks remain
                if (!HasBlocksRemaining) {
                    LiveCommand = "Session Complete";
                    HasLiveStatus = true;
                    global::NINA.Core.Utility.Logger.Info("AstroPM | All blocks finished — session complete");
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
            var apiService = new AstroPMApiService();
            try {
                var response = await apiService.ListTargetsAsync(settings.SyncToken, "Active", token);
                if (response.Success && response.Targets != null) {
                    targets = response.Targets;
                    TargetCacheService.Save(targets);
                    Logger.Info($"AstroPM | Fetched {targets.Count} targets from cloud");
                } else {
                    Logger.Warning($"AstroPM | Cloud error: {response.Message}, falling back to cache");
                }
            } catch (Exception ex) {
                Logger.Warning($"AstroPM | Cloud fetch failed ({ex.Message}), falling back to cache");
            }

            if (targets == null) {
                var cache = TargetCacheService.Load();
                if (cache != null) {
                    targets = cache.Targets;
                    var age = TargetCacheService.AgeDescription(cache.FetchedUtc);
                    Logger.Info($"AstroPM | Using cached targets (fetched {age})");
                } else {
                    Notification.ShowError("Astro PM: No target data available from cloud or cache.");
                    return;
                }
            }

            targets = targets.Where(t => t.Panels != null && t.Panels.Any(p =>
                p.ExposureSets != null && p.ExposureSets.Any(es => es.Remaining > 0))).ToList();

            if (!string.IsNullOrEmpty(settings.LocationFilter))
                targets = targets.Where(t => string.Equals(t.LocationName, settings.LocationFilter, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!string.IsNullOrEmpty(settings.TelescopeFilter))
                targets = targets.Where(t => string.Equals(t.TelescopeName, settings.TelescopeFilter, StringComparison.OrdinalIgnoreCase)).ToList();

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
            // An observing night spans two calendar dates (evening → dawn).
            // Before dawn (~6 AM) we're still in last night's session → use yesterday.
            // After dawn we want tonight's schedule → use today.
            var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);
            var date = localNow.Hour < 6 ? DateTime.Today.AddDays(-1) : DateTime.Today;
            global::NINA.Core.Utility.Logger.Info(
                $"AstroPM | BuildSchedule: date={date:yyyy-MM-dd} (local={localNow:HH:mm}), lat={latDeg:F4}, lon={lonDeg:F4}, tz={tz.Id}, targets={targets.Count}");
            var slots = SessionScheduler.BuildTimeSlots(date, latDeg, lonDeg, tz);
            var profiles = SessionScheduler.BuildTargetProfiles(targets, slots, latDeg, lonDeg, settings.MosaicPanelPreference);

            foreach (var p in profiles)
                global::NINA.Core.Utility.Logger.Info($"AstroPM | Profile: {p.DisplayName} PanelIdx={p.PanelIndex} LA={p.RemainingLunarFreeSec / 60:F0}m NonLA={p.RemainingNonLunarSec / 60:F0}m window={p.WindowStartSlot}-{p.WindowEndSlot}");

            if (profiles.Count == 0) {
                Notification.ShowWarning("Astro PM: No targets are visible tonight from this location.");
                return;
            }

            foreach (var p in profiles) p.AllocatedSec = 0;

            // Priority order from Project.Priority (1 = highest, 0 = unset → last),
            // synced from the Astro PM cloud. Drives the Manual Priority strategy.
            var order = Enumerable.Range(0, profiles.Count)
                .OrderBy(i => profiles[i].Target.Priority == 0 ? int.MaxValue : profiles[i].Target.Priority)
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

            // Check constraints — skip if "Start Now" was pressed (test mode)
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

            // ── Fire "Before Target" triggers ──
            var parentForTargetTriggers = Parent as SequenceContainer;
            if (parentForTargetTriggers != null) {
                foreach (var trigger in parentForTargetTriggers.GetTriggersSnapshot()) {
                    if (trigger is AstroPMBeforeTargetTrigger beforeTarget)
                        await beforeTarget.FireIfNeeded(block, progress, token);
                }
            }

            // ── Phase 1: Slew + center + guide (with retry/skip on failure) ──
            if (_blockSummaries != null && _currentBlockIndex < _blockSummaries.Count)
                _blockSummaries[_currentBlockIndex].Status = "slewing...";

            Action<string, TargetBlock> updateSimple = (cmd, b) => UpdateLiveStatus(cmd, b);

            // Slew retries are spaced widely so a passing cloud band (which defeats
            // plate solving with "not enough stars") can clear before we give up on the block.
            const int maxSlewRetries = 5;
            const int slewRetryDelaySec = 600;
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
                        // Don't bother waiting if the retry would land past the block's window
                        if (DateTime.UtcNow.AddSeconds(slewRetryDelaySec) >= block.UtcEnd) {
                            global::NINA.Core.Utility.Logger.Warning(
                                $"AstroPM | Block window for {block.TargetName} ends before next retry — giving up early");
                            break;
                        }
                        UpdateLiveStatus("Slew Error", block);
                        progress?.Report(new ApplicationStatus {
                            Status = $"Astro PM: Slew failed — retrying in {slewRetryDelaySec / 60} min ({attempt}/{maxSlewRetries})..."
                        });
                        await Task.Delay(TimeSpan.FromSeconds(slewRetryDelaySec), token);
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
            while (DateTime.UtcNow < block.UtcEnd && !_skipBlock) {
                token.ThrowIfCancellationRequested();

                // Find the schedule entry we should be on right now.
                // Walk backward from end to find the last entry whose UtcTime <= now.
                var now = DateTime.UtcNow;
                int targetIndex = -1;
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

                var entry = actionEntries[targetIndex];
                var (filterName, exposureSec, gain, offset, binX, binY) = ParseExposureEntry(entry);

                if (!filterImageCount.ContainsKey(filterName))
                    filterImageCount[filterName] = 0;
                int filterSub = filterImageCount[filterName] + 1;

                var exposureItem = new AstroPMTakeExposureItem(block, filterName, exposureSec,
                    gain, offset, binX, binY, filterSub,
                    block.UtcEnd, _sessionEndUtc,
                    _profileService, _filterWheelMediator, _imagingMediator,
                    _imageSaveMediator, _imageHistoryVM,
                    (cmd, b, f, e, s, go) => UpdateLiveStatus(cmd, b, filter: f, exposure: e, sub: s, gainOffset: go),
                    () => { filterImageCount[filterName] = filterSub; OnCaptured(); });

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

            Target = target;

            // Inject target into parent CenterAfterDriftTrigger.
            // AttachNewParent makes the trigger re-discover us as the IDeepSkyObjectContainer
            // via AfterParentChanged, then we also set Coordinates explicitly as a belt-and-suspenders.
            var container = Parent as SequenceContainer;
            while (container != null) {
                foreach (var trigger in container.GetTriggersSnapshot()) {
                    if (trigger is CenterAfterDriftTrigger driftTrigger) {
                        driftTrigger.AttachNewParent(this);
                        driftTrigger.Coordinates = target.InputCoordinates;
                        driftTrigger.Inherited = true;
                    }
                }
                container = container.Parent as SequenceContainer;
            }

            global::NINA.Core.Utility.Logger.Info($"AstroPM | Target set for triggers: {block.TargetName} RA={block.RaHours:F4}h Dec={block.DecDegrees:F3}°");
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

        private static (string Filter, double ExposureSec, int Gain, int Offset, int BinX, int BinY) ParseExposureEntry(SimLogEntry entry) {
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

            return (filter, expSec, gain, offset, binX, binY);
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
