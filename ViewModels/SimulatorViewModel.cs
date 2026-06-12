using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using AstroPM.NINA.Plugin.Models;
using AstroPM.NINA.Plugin.Services;
using System.Collections.Specialized;

namespace AstroPM.NINA.Plugin.ViewModels {

    public class SimulatorViewModel : INotifyPropertyChanged {

        private readonly AstroPMApiService _apiService = new AstroPMApiService();

        private List<ProjectTarget> _allTargets = new List<ProjectTarget>();
        private List<TimeSlot> _slots;
        private List<TargetProfile> _profiles;
        private List<SimLogEntry> _log;
        // Custom horizon (.hrz) from the NINA profile, captured per simulation run —
        // drives the engine's usable-slot check and the card constraint rows.
        private HorizonProfile _customHorizon;

        private DateTime _selectedDate = DateTime.Now.Hour < 7 ? DateTime.Today.AddDays(-1) : DateTime.Today;
        private string _selectedLocation;
        private string _selectedTelescope;
        private string _selectedStatus;
        private ObservableCollection<string> _availableLocations = new ObservableCollection<string>();
        private ObservableCollection<string> _availableTelescopes = new ObservableCollection<string>();
        private ObservableCollection<string> _availableStatuses = new ObservableCollection<string>();
        private ObservableCollection<TargetCardModel> _targetCards = new ObservableCollection<TargetCardModel>();
        private ObservableCollection<SimLogEntry> _logEntries = new ObservableCollection<SimLogEntry>();
        private SimLogEntry _selectedLogEntry;
        private string _summaryText = "";
        private string _statusText = "Select a date and click Simulate.";
        private string _cacheStatusText = "";
        private bool _cacheAvailable;
        private bool _hasResults;
        private bool _isSimulating;
        private double _latitude;
        private double _longitude;
        private bool _ditherEnabled;
        private int _ditherEvery;
        private bool _filterSwitchEnabled;
        private int _filterSwitchCount;
        private bool _bonusImagesEnabled;
        private bool _mosaicPanelPreference;
        private List<SortCriteria> _sortChain;
        private ObservableCollection<SortChipItem> _sortChainItems = new ObservableCollection<SortChipItem>();
        private ImagingStrategy _strategy = ImagingStrategy.SharedTime;
        private double _filterSwitchTolerance = 0.5;
        private string _strategyDescription = "";

        // Uniform with the AstroPM desktop simulator — keep wording in sync
        public static readonly Dictionary<ImagingStrategy, string> StrategyLabels = new Dictionary<ImagingStrategy, string> {
            [ImagingStrategy.SharedTime] = "Proportional Time Across All Targets",
            [ImagingStrategy.ManualPriority] = "Manual Priority Targeting",
        };

        public static readonly Dictionary<ImagingStrategy, string> StrategyDescriptions = new Dictionary<ImagingStrategy, string> {
            [ImagingStrategy.SharedTime] = "Divides time proportionally based on each target's remaining work. Targets with more work get more time. LA filters prioritized during moon-down.",
            [ImagingStrategy.ManualPriority] = "Images each target continuously while usable. Lunar avoidance filters prioritized when moon is down. Targets imaged in priority order.",
        };

        public static readonly Color[] TargetCurveColors = {
            Color.FromRgb(0x4F, 0xC3, 0xF7), // Light blue
            Color.FromRgb(0xFF, 0xB7, 0x4D), // Orange
            Color.FromRgb(0xCE, 0x93, 0xD8), // Lavender
            Color.FromRgb(0xF0, 0x62, 0x92), // Rose
            Color.FromRgb(0x4D, 0xB6, 0xAC), // Teal
            Color.FromRgb(0xFF, 0xD5, 0x4F), // Yellow
            Color.FromRgb(0xA1, 0x88, 0x7F), // Tan
            Color.FromRgb(0x90, 0xA4, 0xAE), // Blue-gray
            Color.FromRgb(0xE5, 0x73, 0x73), // Red
            Color.FromRgb(0x79, 0x86, 0xCB), // Indigo
            Color.FromRgb(0xAE, 0xD5, 0x81), // Light green
            Color.FromRgb(0x81, 0xC7, 0x84), // Green
            Color.FromRgb(0xFF, 0x8A, 0x65), // Deep orange
            Color.FromRgb(0x4D, 0xD0, 0xE1), // Cyan
            Color.FromRgb(0xBA, 0x68, 0xC8), // Purple
            Color.FromRgb(0xDC, 0xE7, 0x75), // Lime
            Color.FromRgb(0xFF, 0xAB, 0x91), // Salmon
            Color.FromRgb(0x80, 0xDE, 0xEA), // Light cyan
            Color.FromRgb(0xEF, 0x53, 0x50), // Bright red
            Color.FromRgb(0x26, 0xA6, 0x9A), // Dark teal
            Color.FromRgb(0xEC, 0x40, 0x7A), // Pink
            Color.FromRgb(0xAB, 0x47, 0xBC), // Deep purple
            Color.FromRgb(0x66, 0xBB, 0x6A), // Mid green
            Color.FromRgb(0x42, 0xA5, 0xF5), // Blue
            Color.FromRgb(0xFF, 0xCA, 0x28), // Amber
        };

        private Dictionary<int, Color> _profileColorMap = new Dictionary<int, Color>();

        public Color GetProfileColor(int profileIndex) {
            return _profileColorMap.TryGetValue(profileIndex, out var c) ? c : TargetCurveColors[profileIndex % TargetCurveColors.Length];
        }

        private static readonly SolidColorBrush PassBrush = new SolidColorBrush(Color.FromRgb(0x4E, 0xC9, 0x3A));
        private static readonly SolidColorBrush FailBrush = new SolidColorBrush(Color.FromRgb(0xE7, 0x4C, 0x3C));
        private static readonly SolidColorBrush DimBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

        private static readonly SolidColorBrush InfoColor = new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7));
        private static readonly SolidColorBrush SlewColor = new SolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x4D));
        private static readonly SolidColorBrush ImageColor = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
        private static readonly SolidColorBrush DitherColor = new SolidColorBrush(Color.FromRgb(0xCE, 0x93, 0xD8));
        private static readonly SolidColorBrush FilterColor = new SolidColorBrush(Color.FromRgb(0x4D, 0xB6, 0xAC));
        private static readonly SolidColorBrush WaitColor = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        private static readonly SolidColorBrush BonusColor = new SolidColorBrush(Color.FromRgb(0x81, 0xC7, 0x84));

        private static readonly Dictionary<string, Color> FilterColorMap = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase) {
            { "L", Color.FromRgb(0xDD, 0xDD, 0xDD) }, { "Lum", Color.FromRgb(0xDD, 0xDD, 0xDD) },
            { "Luminance", Color.FromRgb(0xDD, 0xDD, 0xDD) },
            { "R", Color.FromRgb(0xE5, 0x42, 0x42) }, { "Red", Color.FromRgb(0xE5, 0x42, 0x42) },
            { "G", Color.FromRgb(0x4C, 0xAF, 0x50) }, { "Green", Color.FromRgb(0x4C, 0xAF, 0x50) },
            { "B", Color.FromRgb(0x42, 0x8B, 0xF5) }, { "Blue", Color.FromRgb(0x42, 0x8B, 0xF5) },
            { "Ha", Color.FromRgb(0xCC, 0x22, 0x22) }, { "H-alpha", Color.FromRgb(0xCC, 0x22, 0x22) },
            { "OIII", Color.FromRgb(0x00, 0x96, 0x88) }, { "O-III", Color.FromRgb(0x00, 0x96, 0x88) },
            { "SII", Color.FromRgb(0x9B, 0x1B, 0x1B) }, { "S-II", Color.FromRgb(0x9B, 0x1B, 0x1B) },
        };

        public static readonly Dictionary<SortCriteria, string> SortCriteriaLabels = new Dictionary<SortCriteria, string> {
            [SortCriteria.SettingSoonest] = "Setting Soonest",
            [SortCriteria.Constrained] = "Constrained Windows",
            [SortCriteria.MostRemainingWork] = "Most Remaining Work",
            [SortCriteria.MostLaWork] = "Most LA Work",
            [SortCriteria.LowestPeakAltitude] = "Lowest Peak Altitude",
            [SortCriteria.MosaicGroup] = "Mosaic Grouping",
            [SortCriteria.UserPriority] = "User Card Order",
        };

        public SimulatorViewModel() {
            var settings = AstroPMSettings.Load();
            _ditherEnabled = settings.DitherEnabled;
            _ditherEvery = settings.DitherEvery;
            _filterSwitchEnabled = settings.FilterSwitchEnabled;
            _filterSwitchCount = settings.FilterSwitchCount;
            _filterSwitchTolerance = settings.FilterSwitchTolerance;
            _bonusImagesEnabled = settings.BonusEnabled;
            _mosaicPanelPreference = settings.MosaicPanelPreference;
            _sortChain = ParseSortChain(settings.SortChain);
            if (Enum.TryParse<ImagingStrategy>(settings.Strategy, out var savedStrategy))
                _strategy = savedStrategy;
            _strategyDescription = StrategyDescriptions[_strategy];
            RefreshSortChainItems();

            SimulateCommand = new RelayCommand(async _ => await RunSimulationAsync(), _ => !IsSimulating);
            DatePrevCommand = new RelayCommand(async _ => { SelectedDate = SelectedDate.AddDays(-1); await RunSimulationAsync(); }, _ => !IsSimulating);
            DateNextCommand = new RelayCommand(async _ => { SelectedDate = SelectedDate.AddDays(1); await RunSimulationAsync(); }, _ => !IsSimulating);
            DateTodayCommand = new RelayCommand(async _ => { SelectedDate = DateTime.Now.Hour < 7 ? DateTime.Today.AddDays(-1) : DateTime.Today; await RunSimulationAsync(); }, _ => !IsSimulating);

            RefreshCacheStatus();
        }

        private void RefreshCacheStatus() {
            var cache = TargetCacheService.Load();
            if (cache != null) {
                var localTime = cache.FetchedUtc.ToLocalTime();
                CacheStatusText = $"Cloud Targets Last Fetched: {localTime:MMM d, yyyy h:mm tt}";
                CacheAvailable = true;
            } else {
                CacheStatusText = "Cloud Targets: No data cached yet";
                CacheAvailable = false;
            }
        }

        private void SetCacheOffline(DateTime? fetchedUtc) {
            if (fetchedUtc.HasValue) {
                var localTime = fetchedUtc.Value.ToLocalTime();
                CacheStatusText = $"Using cached targets from {localTime:MMM d, yyyy h:mm tt} until cloud comes back online";
            } else {
                CacheStatusText = "Using in-memory targets — cloud unavailable";
            }
            CacheAvailable = false;
        }

        // ── Properties ──

        public DateTime SelectedDate {
            get => _selectedDate;
            set { _selectedDate = value; OnPropertyChanged(); OnPropertyChanged(nameof(DateDisplay)); }
        }

        public string DateDisplay => _selectedDate.ToString("yyyy-MM-dd");

        public string CacheStatusText {
            get => _cacheStatusText;
            set { _cacheStatusText = value; OnPropertyChanged(); }
        }

        public bool CacheAvailable {
            get => _cacheAvailable;
            set { _cacheAvailable = value; OnPropertyChanged(); }
        }

        public string SelectedLocation {
            get => _selectedLocation;
            set { _selectedLocation = value; OnPropertyChanged(); SaveSimFilters(); }
        }

        public string SelectedTelescope {
            get => _selectedTelescope;
            set { _selectedTelescope = value; OnPropertyChanged(); SaveSimFilters(); }
        }

        public string SelectedStatus {
            get => _selectedStatus;
            set { _selectedStatus = value; OnPropertyChanged(); SaveSimFilters(); }
        }

        public ObservableCollection<string> AvailableLocations {
            get => _availableLocations;
            set { _availableLocations = value; OnPropertyChanged(); }
        }

        public ObservableCollection<string> AvailableTelescopes {
            get => _availableTelescopes;
            set { _availableTelescopes = value; OnPropertyChanged(); }
        }

        public ObservableCollection<string> AvailableStatuses {
            get => _availableStatuses;
            set { _availableStatuses = value; OnPropertyChanged(); }
        }

        public ObservableCollection<TargetCardModel> TargetCards {
            get => _targetCards;
            set { _targetCards = value; OnPropertyChanged(); }
        }

        public void SwitchPanel(int profileIndex, int panelIndex) {
            if (_targetCards == null) return;
            var card = _targetCards.FirstOrDefault(c => c.ProfileIndex == profileIndex);
            if (card == null || panelIndex >= card.PanelFilterGroups.Count) return;

            card.SelectedPanelIndex = panelIndex;
            card.VisibleFilters = card.PanelFilterGroups[panelIndex];

            var activeBg = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
            var inactiveBg = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
            foreach (var tab in card.PanelNames) {
                bool active = tab.PanelIndex == panelIndex;
                tab.Background = active ? activeBg : inactiveBg;
                tab.Foreground = active ? new SolidColorBrush(Colors.White) : new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                tab.BorderColor = active ? activeBg : inactiveBg;
            }

            TargetCards = new ObservableCollection<TargetCardModel>(_targetCards);
        }

        public ObservableCollection<SimLogEntry> LogEntries {
            get => _logEntries;
            set { _logEntries = value; OnPropertyChanged(); }
        }

        public SimLogEntry SelectedLogEntry {
            get => _selectedLogEntry;
            set {
                _selectedLogEntry = value;
                OnPropertyChanged();
                UpdateCurrentInstruction(value);
                ScrubberPositionChanged?.Invoke(value);
            }
        }

        private string _currentInstructionTarget = "";
        private string _currentInstructionCommand = "";
        private string _currentInstructionFilter = "";
        private string _currentInstructionExposure = "";
        private string _currentInstructionRA = "";
        private string _currentInstructionDEC = "";
        private string _currentInstructionRotation = "";
        private string _currentInstructionPanel = "";
        private string _currentInstructionSub = "";
        private string _currentInstructionGainOffset = "";

        public string CurrentInstructionTarget { get => _currentInstructionTarget; set { _currentInstructionTarget = value; OnPropertyChanged(); } }
        public string CurrentInstructionCommand { get => _currentInstructionCommand; set { _currentInstructionCommand = value; OnPropertyChanged(); } }
        public string CurrentInstructionFilter { get => _currentInstructionFilter; set { _currentInstructionFilter = value; OnPropertyChanged(); } }
        public string CurrentInstructionExposure { get => _currentInstructionExposure; set { _currentInstructionExposure = value; OnPropertyChanged(); } }
        public string CurrentInstructionRA { get => _currentInstructionRA; set { _currentInstructionRA = value; OnPropertyChanged(); } }
        public string CurrentInstructionDEC { get => _currentInstructionDEC; set { _currentInstructionDEC = value; OnPropertyChanged(); } }
        public string CurrentInstructionRotation { get => _currentInstructionRotation; set { _currentInstructionRotation = value; OnPropertyChanged(); } }
        public string CurrentInstructionPanel { get => _currentInstructionPanel; set { _currentInstructionPanel = value; OnPropertyChanged(); } }
        public string CurrentInstructionSub { get => _currentInstructionSub; set { _currentInstructionSub = value; OnPropertyChanged(); } }
        public string CurrentInstructionGainOffset { get => _currentInstructionGainOffset; set { _currentInstructionGainOffset = value; OnPropertyChanged(); } }

        private void UpdateCurrentInstruction(SimLogEntry entry) {
            if (entry == null) return;

            string target = entry.Target;
            string command = entry.Command;
            string filter = entry.Filter;
            string exposure = entry.Exposure;
            string ra = entry.RA;
            string dec = entry.DEC;
            string rotation = entry.Rotation;
            string panel = entry.Panel;
            string sub = entry.SubNum;
            string gainOffset = "";

            if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(ra)) {
                var ctx = FindContextEntry(entry);
                if (ctx != null) {
                    if (string.IsNullOrEmpty(target)) target = ctx.Target;
                    if (string.IsNullOrEmpty(ra)) ra = ctx.RA;
                    if (string.IsNullOrEmpty(dec)) dec = ctx.DEC;
                    if (string.IsNullOrEmpty(rotation)) rotation = ctx.Rotation;
                    if (string.IsNullOrEmpty(panel)) panel = ctx.Panel;
                }
            }

            if (!string.IsNullOrEmpty(entry.Gain) || !string.IsNullOrEmpty(entry.Offset)) {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(entry.Gain)) parts.Add($"G{entry.Gain}");
                if (!string.IsNullOrEmpty(entry.Offset)) parts.Add($"O{entry.Offset}");
                if (!string.IsNullOrEmpty(entry.Bin)) parts.Add($"Bin {entry.Bin}");
                gainOffset = string.Join("  ", parts);
            }

            CurrentInstructionTarget = target ?? "";
            CurrentInstructionCommand = command ?? "";
            CurrentInstructionFilter = filter ?? "";
            CurrentInstructionExposure = exposure ?? "";
            CurrentInstructionRA = ra ?? "";
            CurrentInstructionDEC = dec ?? "";
            CurrentInstructionRotation = rotation ?? "";
            CurrentInstructionPanel = panel ?? "";
            CurrentInstructionSub = sub ?? "";
            CurrentInstructionGainOffset = gainOffset;
        }

        private SimLogEntry FindContextEntry(SimLogEntry current) {
            if (_log == null) return null;
            int idx = _log.IndexOf(current);
            if (idx < 0) return null;
            for (int i = idx - 1; i >= 0; i--) {
                var e = _log[i];
                if (!string.IsNullOrEmpty(e.RA) && !string.IsNullOrEmpty(e.Target))
                    return e;
            }
            return null;
        }

        public string SummaryText {
            get => _summaryText;
            set { _summaryText = value; OnPropertyChanged(); }
        }

        public string StatusText {
            get => _statusText;
            set { _statusText = value; OnPropertyChanged(); }
        }

        public bool HasResults {
            get => _hasResults;
            set { _hasResults = value; OnPropertyChanged(); }
        }

        public bool IsSimulating {
            get => _isSimulating;
            set { _isSimulating = value; OnPropertyChanged(); (SimulateCommand as RelayCommand)?.RaiseCanExecuteChanged(); }
        }

        public bool DitherEnabled {
            get => _ditherEnabled;
            set { _ditherEnabled = value; OnPropertyChanged(); SaveSimSettings(); }
        }

        public int DitherEvery {
            get => _ditherEvery;
            set { _ditherEvery = value; OnPropertyChanged(); SaveSimSettings(); }
        }

        public bool FilterSwitchEnabled {
            get => _filterSwitchEnabled;
            set { _filterSwitchEnabled = value; OnPropertyChanged(); SaveSimSettings(); }
        }

        public int FilterSwitchCount {
            get => _filterSwitchCount;
            set { _filterSwitchCount = value; OnPropertyChanged(); SaveSimSettings(); }
        }

        public double FilterSwitchTolerance {
            get => _filterSwitchTolerance;
            set { _filterSwitchTolerance = value; OnPropertyChanged(); SaveSimSettings(); }
        }

        public List<KeyValuePair<ImagingStrategy, string>> StrategyOptions { get; } =
            StrategyLabels.ToList();

        public List<KeyValuePair<double, string>> ToleranceOptions { get; } =
            new List<KeyValuePair<double, string>> {
                new KeyValuePair<double, string>(0.0, "0%"),
                new KeyValuePair<double, string>(0.1, "10%"),
                new KeyValuePair<double, string>(0.2, "20%"),
                new KeyValuePair<double, string>(0.3, "30%"),
                new KeyValuePair<double, string>(0.4, "40%"),
                new KeyValuePair<double, string>(0.5, "50%"),
                new KeyValuePair<double, string>(0.6, "60%"),
                new KeyValuePair<double, string>(0.7, "70%"),
                new KeyValuePair<double, string>(0.8, "80%"),
                new KeyValuePair<double, string>(0.9, "90%"),
                new KeyValuePair<double, string>(1.0, "100%"),
            };

        public ImagingStrategy Strategy {
            get => _strategy;
            set {
                if (_strategy == value) return;
                _strategy = value;
                StrategyDescription = StrategyDescriptions[value];
                OnPropertyChanged();
                OnPropertyChanged(nameof(PriorityHint));
                SaveSimSettings();
                _ = RunSimulationAsync();
            }
        }

        // Uniform with the AstroPM desktop simulator's Step 2 hint
        public string PriorityHint => _strategy == ImagingStrategy.ManualPriority
            ? "—  ordered by each project's Priority setting (1 = highest)"
            : "";

        public bool BonusImagesEnabled {
            get => _bonusImagesEnabled;
            set {
                if (_bonusImagesEnabled == value) return;
                _bonusImagesEnabled = value; OnPropertyChanged(); SaveSimSettings(); _ = RunSimulationAsync();
            }
        }

        public bool MosaicPanelPreference {
            get => _mosaicPanelPreference;
            set {
                if (_mosaicPanelPreference == value) return;
                _mosaicPanelPreference = value; OnPropertyChanged(); SaveSimSettings(); _ = RunSimulationAsync();
            }
        }

        public ObservableCollection<SortChipItem> SortChainItems {
            get => _sortChainItems;
            set { _sortChainItems = value; OnPropertyChanged(); }
        }

        public void ReorderSortChain(SortCriteria dropped, SortCriteria target) {
            int fromIdx = _sortChain.IndexOf(dropped);
            int toIdx = _sortChain.IndexOf(target);
            if (fromIdx < 0 || toIdx < 0 || fromIdx == toIdx) return;

            _sortChain.RemoveAt(fromIdx);
            _sortChain.Insert(toIdx, dropped);

            RefreshSortChainItems();
            SaveSimSettings();
            _ = RunSimulationAsync();
        }

        private void RefreshSortChainItems() {
            var items = new ObservableCollection<SortChipItem>();
            for (int i = 0; i < _sortChain.Count; i++) {
                var c = _sortChain[i];
                items.Add(new SortChipItem {
                    Index = $"{i + 1}.",
                    Label = SortCriteriaLabels.TryGetValue(c, out var lbl) ? lbl : c.ToString(),
                    Criteria = c,
                });
            }
            SortChainItems = items;
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

        private List<SortCriteria> BuildMoonDownChain() {
            var chain = new List<SortCriteria> { SortCriteria.MostLaWork };
            foreach (var c in _sortChain)
                if (c != SortCriteria.MostLaWork) chain.Add(c);
            return chain;
        }

        public string StrategyDescription {
            get => _strategyDescription;
            set { _strategyDescription = value; OnPropertyChanged(); }
        }

        public List<TimeSlot> Slots => _slots;
        public List<TargetProfile> Profiles => _profiles;
        public List<SimLogEntry> Log => _log;

        // ── Commands ──

        public ICommand SimulateCommand { get; }
        public ICommand DatePrevCommand { get; }
        public ICommand DateNextCommand { get; }
        public ICommand DateTodayCommand { get; }

        // ── Events ──

        public event Action<SimLogEntry> ScrubberPositionChanged;
        public event Action ChartDataReady;

        // ── Public Methods ──

        public void SetObservatoryLocation(double lat, double lon) {
            _latitude = lat;
            _longitude = lon;
            _ = PreloadTargetsAsync();
        }

        private async Task PreloadTargetsAsync() {
            var settings = AstroPMSettings.Load();
            if (string.IsNullOrEmpty(settings.SyncToken)) return;
            try {
                var response = await _apiService.ListTargetsAsync(settings.SyncToken, null);
                if (response.Success && response.Targets != null) {
                    _allTargets = response.Targets;
                    RebuildFilters();
                }
            } catch { }
        }

        public SolidColorBrush GetLogRowColor(SimLogEntry entry) {
            if (entry == null) return ImageColor;
            return entry.Command switch {
                "Start" or "End" or "Info" => InfoColor,
                "Slew" => SlewColor,
                "Image" => ImageColor,
                "Bonus" => BonusColor,
                "Dither" => DitherColor,
                "Filter" => FilterColor,
                "Wait" => WaitColor,
                _ => ImageColor,
            };
        }

        public void SelectLogEntryAtTime(DateTime utc) {
            if (_log == null || _log.Count == 0) return;
            SimLogEntry best = null;
            foreach (var entry in _log) {
                if (entry.UtcTime == default) continue;
                if (entry.UtcTime <= utc) best = entry;
                else break;
            }
            if (best != null) SelectedLogEntry = best;
        }

        // ── Core Simulation ──

        private async Task RunSimulationAsync() {
            IsSimulating = true;
            StatusText = "Fetching targets from cloud...";

            var settings = AstroPMSettings.Load();
            if (string.IsNullOrEmpty(settings.SyncToken)) {
                StatusText = "No sync token configured. Open Connection settings above.";
                IsSimulating = false;
                return;
            }

            try {
                var response = await _apiService.ListTargetsAsync(settings.SyncToken, null);
                if (response.Success && response.Targets != null) {
                    _allTargets = response.Targets;
                    TargetCacheService.Save(_allTargets);
                    RebuildFilters();
                    RefreshCacheStatus();
                } else {
                    if (_allTargets.Count == 0) {
                        var cache = TargetCacheService.Load();
                        if (cache != null) {
                            _allTargets = cache.Targets;
                            RebuildFilters();
                            SetCacheOffline(cache.FetchedUtc);
                        } else {
                            StatusText = response.Message ?? "Failed to fetch targets.";
                            CacheStatusText = "Cloud Targets: No data cached yet";
                            CacheAvailable = false;
                            IsSimulating = false;
                            return;
                        }
                    } else {
                        SetCacheOffline(null);
                    }
                }
            } catch (Exception) {
                if (_allTargets.Count == 0) {
                    var cache = TargetCacheService.Load();
                    if (cache != null) {
                        _allTargets = cache.Targets;
                        RebuildFilters();
                        SetCacheOffline(cache.FetchedUtc);
                    } else {
                        StatusText = "No internet connection and no cached targets.";
                        CacheStatusText = "Cloud Targets: No data cached yet";
                        CacheAvailable = false;
                        IsSimulating = false;
                        return;
                    }
                } else {
                    SetCacheOffline(null);
                }
            }

            int totalFetched = _allTargets.Count;
            int withExposures = _allTargets.Count(t =>
                t.Panels != null && t.Panels.Any(p =>
                    p.ExposureSets != null && p.ExposureSets.Count > 0));
            int withRemaining = _allTargets.Count(t =>
                t.Panels != null && t.Panels.Any(p =>
                    p.ExposureSets != null && p.ExposureSets.Any(es => es.Remaining > 0)));

            StatusText = $"Fetched {totalFetched} targets ({withExposures} with exposures, {withRemaining} with remaining work). Simulating...";

            await Task.Run(() => RunSimulation());

            Application.Current?.Dispatcher?.Invoke(() => {
                BuildTargetCards();
                BuildLogEntries();
                UpdateSummary();
                HasResults = true;
                ChartDataReady?.Invoke();
                StatusText = $"Schedule generated — {_log.Count(e => e.Command == "Image")} subs planned.";
            });

            IsSimulating = false;
        }

        private void RunSimulation() {
            var targets = FilterTargets();

            if (targets.Count == 0) {
                _slots = new List<TimeSlot>();
                _profiles = new List<TargetProfile>();
                _log = new List<SimLogEntry> { new SimLogEntry { Command = "Info", Target = "No matching targets." } };
                return;
            }

            double lat = _latitude;
            double lon = _longitude;
            if (Math.Abs(lat) < 0.001 && Math.Abs(lon) < 0.001) {
                _slots = new List<TimeSlot>();
                _profiles = new List<TargetProfile>();
                _log = new List<SimLogEntry> { new SimLogEntry { Command = "Info", Target = "Observatory location not set in NINA profile." } };
                return;
            }

            var tz = TimeZoneInfo.Local;
            _slots = SessionScheduler.BuildTimeSlots(_selectedDate, lat, lon, tz);

            // NINA's custom horizon (.hrz) — same obstruction check the live instruction
            // set applies, so the simulator preview matches what will actually run.
            _customHorizon = HorizonProfile.LoadFromNinaProfile();
            _profiles = SessionScheduler.BuildTargetProfiles(targets, _slots, lat, lon, _mosaicPanelPreference, _customHorizon);

            if (_profiles.Count == 0) {
                _log = new List<SimLogEntry> { new SimLogEntry { Command = "Info", Target = "No targets visible tonight." } };
                return;
            }

            foreach (var p in _profiles) p.AllocatedSec = 0;

            // Priority order from Project.Priority (1 = highest, 0 = unset → last),
            // synced from the Astro PM cloud. Drives the Manual Priority strategy.
            var order = Enumerable.Range(0, _profiles.Count)
                .OrderBy(i => _profiles[i].Target.Priority == 0 ? int.MaxValue : _profiles[i].Target.Priority)
                .ThenBy(i => i)
                .ToList();
            var matrix = ScheduleEngine.BuildMatrix(_slots, _profiles, order);

            if (matrix.FirstUsableSlot < 0) {
                _log = new List<SimLogEntry> { new SimLogEntry { Command = "Info", Target = "No usable time window for any target." } };
                return;
            }

            ScheduleEngine.ComputeOverlap(matrix);
            ScheduleEngine.OrganizeMoonBlocks(matrix);

            if (_strategy == ImagingStrategy.ManualPriority)
                ScheduleEngine.PaintSlotsGreedy(matrix, order, _bonusImagesEnabled);
            else
                ScheduleEngine.PaintSlots(matrix, _sortChain, BuildMoonDownChain(), _bonusImagesEnabled);

            try {
                var diag = ScheduleEngine.DumpDiagnostic(matrix, _sortChain, tz);
                var diagPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "astropm_nina_diag.txt");
                System.IO.File.WriteAllText(diagPath, diag);
            } catch { }

            var state = new ScheduleSessionState();
            _log = ScheduleEngine.WalkToLog(matrix, state, tz,
                _ditherEnabled, _ditherEvery, _filterSwitchEnabled, _filterSwitchCount, _sortChain,
                bonusEnabled: _bonusImagesEnabled,
                filterSwitchTolerance: _filterSwitchTolerance);
        }

        private List<ProjectTarget> FilterTargets() {
            var settings = AstroPMSettings.Load();
            var targets = _allTargets.Where(t =>
                t.Panels != null && t.Panels.Any(p =>
                    p.ExposureSets != null && p.ExposureSets.Any(es => es.Remaining > 0)));

            // Always filter to Active status
            targets = targets.Where(t => string.Equals(t.Status, "Active", StringComparison.OrdinalIgnoreCase));

            // Use universal settings for location/telescope
            if (!string.IsNullOrEmpty(settings.LocationFilter))
                targets = targets.Where(t => string.Equals(t.LocationName, settings.LocationFilter, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(settings.TelescopeFilter))
                targets = targets.Where(t => string.Equals(t.TelescopeName, settings.TelescopeFilter, StringComparison.OrdinalIgnoreCase));

            return targets.ToList();
        }

        private void RebuildFilters() {
            // Simulator always uses "Active" status and reads location/telescope
            // from the universal Astro PM Settings. No local dropdowns needed.
        }

        private void SaveSimFilters() {
            // Simulator now reads filters from universal Astro PM Settings — nothing to save locally.
        }

        private void SaveSimSettings() {
            var settings = AstroPMSettings.Load();
            settings.DitherEnabled = _ditherEnabled;
            settings.DitherEvery = _ditherEvery;
            settings.FilterSwitchEnabled = _filterSwitchEnabled;
            settings.FilterSwitchCount = _filterSwitchCount;
            settings.FilterSwitchTolerance = _filterSwitchTolerance;
            settings.BonusEnabled = _bonusImagesEnabled;
            settings.MosaicPanelPreference = _mosaicPanelPreference;
            settings.SortChain = string.Join(",", _sortChain);
            settings.Strategy = _strategy.ToString();
            settings.Save();
        }

        // ── Card Building ──

        private void BuildTargetCards() {
            _originalCards = null;
            var cards = new ObservableCollection<TargetCardModel>();
            if (_profiles == null) { TargetCards = cards; return; }

            var tz = TimeZoneInfo.Local;

            // Manual Priority: order cards by each project's Priority setting and
            // number them. Proportional: order by first slew time to match AstroPM.
            List<int> orderedIndices;
            var priorityRank = new Dictionary<int, int>(); // profileIndex → display rank (1-based)
            if (_strategy == ImagingStrategy.ManualPriority) {
                orderedIndices = Enumerable.Range(0, _profiles.Count)
                    .OrderBy(i => _profiles[i].Target.Priority == 0 ? int.MaxValue : _profiles[i].Target.Priority)
                    .ThenBy(i => i)
                    .ToList();
                for (int rank = 0; rank < orderedIndices.Count; rank++)
                    priorityRank[orderedIndices[rank]] = rank + 1;
            } else {
                var firstSlewTime = new Dictionary<int, DateTime>();
                if (_log != null) {
                    foreach (var entry in _log.Where(e => e.Command == "Slew")) {
                        int pIdx = _profiles.FindIndex(p => p.DisplayName == entry.Target);
                        if (pIdx >= 0 && !firstSlewTime.ContainsKey(pIdx))
                            firstSlewTime[pIdx] = entry.UtcTime;
                    }
                }
                orderedIndices = Enumerable.Range(0, _profiles.Count)
                    .OrderBy(i => firstSlewTime.ContainsKey(i) ? firstSlewTime[i] : DateTime.MaxValue)
                    .ToList();
            }

            _profileColorMap = new Dictionary<int, Color>();
            var projectColorMap = new Dictionary<string, Color>();
            int colorIdx = 0;
            for (int oi = 0; oi < orderedIndices.Count; oi++) {
                int i = orderedIndices[oi];
                string projectName = _profiles[i].Target.TargetName;
                if (!projectColorMap.TryGetValue(projectName, out var color)) {
                    color = TargetCurveColors[colorIdx % TargetCurveColors.Length];
                    projectColorMap[projectName] = color;
                    colorIdx++;
                }
                _profileColorMap[i] = color;
            }

            foreach (int i in orderedIndices) {
                var prof = _profiles[i];
                var target = prof.Target;
                var c = prof.Constraints;
                var color = _profileColorMap[i];

                string allocTime = prof.AllocatedSec >= 3600
                    ? $"{prof.AllocatedSec / 3600.0:F1}h"
                    : $"{prof.AllocatedSec / 60.0:F0}m";

                string window = "—";
                string altRange = "—";
                if (prof.WindowStartSlot >= 0 && prof.WindowEndSlot >= 0) {
                    var ws = TimeZoneInfo.ConvertTimeFromUtc(_slots[prof.WindowStartSlot].UtcStart, tz);
                    var we = TimeZoneInfo.ConvertTimeFromUtc(_slots[prof.WindowEndSlot].UtcStart.AddMinutes(5), tz);
                    double hrs = (prof.WindowEndSlot - prof.WindowStartSlot + 1) * 5.0 / 60.0;
                    window = $"{ws:HH:mm} – {we:HH:mm} ({hrs:F1}h)";

                    double minAlt = 90, maxAlt = 0;
                    for (int s = prof.WindowStartSlot; s <= prof.WindowEndSlot; s++) {
                        if (prof.AltitudePerSlot[s] < minAlt) minAlt = prof.AltitudePerSlot[s];
                        if (prof.AltitudePerSlot[s] > maxAlt) maxAlt = prof.AltitudePerSlot[s];
                    }
                    altRange = $"{minAlt:F0}° – {maxAlt:F0}°";
                }

                int midSlot = _slots.Count / 2;
                double moonSep = midSlot < prof.MoonSepPerSlot.Length
                    ? prof.MoonSepPerSlot[midSlot] : 0;

                bool multiPanel = target.Panels.Count > 1 && !prof.PanelIndex.HasValue;
                var panelFilterGroups = new List<List<FilterPillModel>>();
                var panelTabs = new List<PanelTabModel>();
                var activeBg = new SolidColorBrush(Color.FromRgb(0x00, 0x7A, 0xCC));
                var inactiveBg = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                var activeFg = new SolidColorBrush(Colors.White);
                var inactiveFg = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));

                int panelGroupIdx = 0;
                for (int pi = 0; pi < target.Panels.Count; pi++) {
                    if (prof.PanelIndex.HasValue && pi != prof.PanelIndex.Value) continue;
                    var panel = target.Panels[pi];

                    var panelFilters = new List<FilterPillModel>();
                    foreach (var es in panel.ExposureSets) {
                        int remaining = Math.Max(0, es.PlannedCount - es.AcceptedCount);
                        if (remaining <= 0) continue;

                        double pct = es.PlannedCount > 0 ? (double)es.AcceptedCount / es.PlannedCount * 100 : 0;
                        string detail = $"{remaining}/{es.PlannedCount} remaining × {es.ExposureLengthSec:F0}s";
                        if (es.HasMoonAvoidance) detail += " [LA]";

                        var chipColor = FilterColorMap.TryGetValue(es.FilterName, out var fc)
                            ? new SolidColorBrush(fc)
                            : new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

                        panelFilters.Add(new FilterPillModel {
                            FilterLabel = es.FilterName,
                            ExposureDetail = detail,
                            StatusIcon = "✓",
                            StatusColor = PassBrush,
                            ChipColor = chipColor,
                            ProgressPercent = pct,
                            IsLunarAvoid = es.HasMoonAvoidance,
                            SourceExposureSet = es,
                        });
                    }

                    if (panelFilters.Count == 0) continue;

                    bool isFirst = panelGroupIdx == 0;
                    string tabLabel = multiPanel ? $"Panel {panelGroupIdx + 1}" : "All";
                    panelTabs.Add(new PanelTabModel {
                        Label = tabLabel, PanelIndex = panelGroupIdx,
                        Background = isFirst ? activeBg : inactiveBg,
                        Foreground = isFirst ? activeFg : inactiveFg,
                        BorderColor = isFirst ? activeBg : inactiveBg,
                    });
                    panelFilterGroups.Add(panelFilters);
                    panelGroupIdx++;
                }

                var checks = BuildConstraintChecks(prof);

                var hasLa = panelFilterGroups.Any(g => g.Any(p => p.IsLunarAvoid));

                cards.Add(new TargetCardModel {
                    Name = prof.DisplayName,
                    PriorityBadge = priorityRank.TryGetValue(i, out var rank) ? $"#{rank}" : "",
                    PriorityBadgeVisibility = priorityRank.ContainsKey(i) ? Visibility.Visible : Visibility.Collapsed,
                    AllocatedTime = allocTime,
                    Window = window,
                    AltitudeRange = altRange,
                    MoonSeparation = $"{moonSep:F0}°",
                    ColorBrush = new SolidColorBrush(color),
                    ProfileIndex = i,
                    PanelFilterGroups = panelFilterGroups,
                    VisibleFilters = panelFilterGroups.Count > 0 ? panelFilterGroups[0] : new List<FilterPillModel>(),
                    PanelNames = panelTabs,
                    IsMultiPanel = multiPanel ? Visibility.Visible : Visibility.Collapsed,
                    SelectedPanelIndex = 0,
                    ConstraintChecks = checks,
                    LaDefinitionVisibility = hasLa ? Visibility.Visible : Visibility.Collapsed,
                });
            }
            TargetCards = cards;
        }

        private List<ConstraintCheckModel> BuildConstraintChecks(TargetProfile prof) {
            var checks = new List<ConstraintCheckModel>();
            var c = prof.Constraints;

            double maxAlt = 0;
            if (prof.WindowStartSlot >= 0)
                for (int s = prof.WindowStartSlot; s <= prof.WindowEndSlot; s++)
                    if (prof.AltitudePerSlot[s] > maxAlt) maxAlt = prof.AltitudePerSlot[s];

            bool altPass = maxAlt >= c.MinTargetAltitude;
            checks.Add(new ConstraintCheckModel {
                Icon = altPass ? "✓" : "✗", IconColor = altPass ? PassBrush : FailBrush,
                Label = "Altitude", Detail = $"Peak {maxAlt:F0}° {(altPass ? "≥" : "<")} {c.MinTargetAltitude:F0}° min",
                DetailColor = DimBrush,
            });

            // Custom horizon (.hrz from the NINA profile) — best clearance over the
            // usable window. Same obstruction math the engine uses for SlotUsable.
            if (_customHorizon != null && prof.WindowStartSlot >= 0) {
                double bestClear = double.MinValue, bestHrz = 0, bestAz = 0;
                for (int s = prof.WindowStartSlot; s <= prof.WindowEndSlot && s < _slots.Count; s++) {
                    double az = AstroCalculator.TargetAzimuthAtTime(
                        _slots[s].UtcStart, prof.Target.RaHours, prof.Target.DecDegrees, _latitude, _longitude);
                    double hrzAlt = _customHorizon.AltitudeAt(az);
                    double clear = prof.AltitudePerSlot[s] - hrzAlt;
                    if (clear > bestClear) { bestClear = clear; bestHrz = hrzAlt; bestAz = az; }
                }
                bool hrzPass = bestClear >= 0;
                checks.Add(new ConstraintCheckModel {
                    Icon = hrzPass ? "✓" : "✗", IconColor = hrzPass ? PassBrush : FailBrush,
                    Label = "Custom Horizon",
                    Detail = hrzPass
                        ? $"Clears {bestHrz:F0}° horizon by {bestClear:F0}° (az {bestAz:F0}°)"
                        : $"{-bestClear:F0}° below {bestHrz:F0}° horizon (az {bestAz:F0}°)",
                    DetailColor = DimBrush,
                });
            }

            double allocHrs = prof.AllocatedSec / 3600.0;
            bool timePass = c.MinTimeOnTargetHrs <= 0 || allocHrs >= c.MinTimeOnTargetHrs;
            checks.Add(new ConstraintCheckModel {
                Icon = timePass ? "✓" : "✗", IconColor = timePass ? PassBrush : FailBrush,
                Label = "Time",
                Detail = c.MinTimeOnTargetHrs > 0
                    ? $"{allocHrs:F1}h {(timePass ? "≥" : "<")} {c.MinTimeOnTargetHrs:F1}h min"
                    : $"{allocHrs:F1}h (no min set)",
                DetailColor = DimBrush,
            });

            bool hasLunar = prof.Target.Panels.SelectMany(p => p.ExposureSets).Any(es => es.HasMoonAvoidance && es.Remaining > 0);
            bool moonPass;
            string moonDetail;
            if (!hasLunar) {
                moonPass = true;
                moonDetail = "No lunar-avoid filters";
            } else {
                int safeSlots = 0;
                for (int s = 0; s < _slots.Count; s++)
                    if (prof.SlotUsable[s] && prof.SlotMoonOk[s]) safeSlots++;
                moonPass = safeSlots > 0;
                double safeHrs = safeSlots * 5.0 / 60.0;
                int midSlot = _slots.Count / 2;
                double sep = midSlot < prof.MoonSepPerSlot.Length ? prof.MoonSepPerSlot[midSlot] : 0;
                moonDetail = moonPass
                    ? $"{safeHrs:F1}h safe, sep {sep:F0}°"
                    : $"No safe slots, sep {sep:F0}°";
            }
            checks.Add(new ConstraintCheckModel {
                Icon = moonPass ? "✓" : "✗", IconColor = moonPass ? PassBrush : FailBrush,
                Label = "Moon", Detail = moonDetail, DetailColor = DimBrush,
            });

            string twilightName = c.SunAltitudeThreshold switch {
                >= -6 => "Civil", >= -12 => "Nautical", _ => "Astronomical",
            };
            double darkSlots = _slots.Count(s => s.SunAltDeg < c.SunAltitudeThreshold);
            double darkHrs = darkSlots * 5.0 / 60.0;
            checks.Add(new ConstraintCheckModel {
                Icon = "✓", IconColor = PassBrush,
                Label = "Darkness", Detail = $"{twilightName} — {darkHrs:F1}h available",
                DetailColor = DimBrush,
            });

            return checks;
        }

        // ── Scrubber-driven card updates ──

        private ObservableCollection<TargetCardModel> _originalCards;

        public void UpdateCardsAtTime(DateTime utc) {
            if (_slots == null || _slots.Count == 0 || _profiles == null || _targetCards == null) return;

            int slotIdx = 0;
            for (int i = 0; i < _slots.Count; i++) {
                if (_slots[i].UtcStart <= utc) slotIdx = i;
                else break;
            }

            if (_originalCards == null)
                _originalCards = _targetCards;

            var slot = _slots[slotIdx];
            bool moonDown = slot.MoonAltDeg <= 0;
            var tz = TimeZoneInfo.Local;

            var updated = new ObservableCollection<TargetCardModel>();
            foreach (var card in _originalCards) {
                int pIdx = card.ProfileIndex;
                if (pIdx < 0 || pIdx >= _profiles.Count) { updated.Add(card); continue; }
                var prof = _profiles[pIdx];

                var updatedFilters = new List<List<FilterPillModel>>();
                double moonSepDeg = slotIdx < prof.MoonSepPerSlot.Length ? prof.MoonSepPerSlot[slotIdx] : 0;
                foreach (var group in card.PanelFilterGroups) {
                    updatedFilters.Add(group.Select(pill => {
                        bool esMoonSafe = pill.SourceExposureSet != null
                            ? SessionScheduler.IsExposureSetMoonSafe(pill.SourceExposureSet, slot, moonSepDeg, prof.Constraints)
                            : !pill.IsLunarAvoid || moonDown;
                        return new FilterPillModel {
                            FilterLabel = pill.FilterLabel,
                            ExposureDetail = pill.ExposureDetail,
                            ChipColor = pill.ChipColor,
                            ProgressPercent = pill.ProgressPercent,
                            IsLunarAvoid = pill.IsLunarAvoid,
                            SourceExposureSet = pill.SourceExposureSet,
                            StatusIcon = esMoonSafe ? "✓" : "✗",
                            StatusColor = esMoonSafe ? PassBrush : FailBrush,
                        };
                    }).ToList());
                }

                var checks = BuildConstraintChecksAtSlot(prof, slotIdx);

                updated.Add(new TargetCardModel {
                    Name = card.Name,
                    AllocatedTime = card.AllocatedTime,
                    Window = card.Window,
                    AltitudeRange = card.AltitudeRange,
                    MoonSeparation = card.MoonSeparation,
                    ColorBrush = card.ColorBrush,
                    ProfileIndex = card.ProfileIndex,
                    PanelFilterGroups = updatedFilters,
                    VisibleFilters = card.SelectedPanelIndex < updatedFilters.Count
                        ? updatedFilters[card.SelectedPanelIndex] : new List<FilterPillModel>(),
                    PanelNames = card.PanelNames,
                    IsMultiPanel = card.IsMultiPanel,
                    SelectedPanelIndex = card.SelectedPanelIndex,
                    ConstraintChecks = checks,
                    LaDefinitionVisibility = card.LaDefinitionVisibility,
                });
            }
            TargetCards = updated;
        }

        public void ResetCardsToDefault() {
            if (_originalCards != null) {
                TargetCards = _originalCards;
                _originalCards = null;
            }
        }

        private List<ConstraintCheckModel> BuildConstraintChecksAtSlot(TargetProfile prof, int slotIdx) {
            var checks = new List<ConstraintCheckModel>();
            var c = prof.Constraints;
            var slot = _slots[slotIdx];
            double alt = prof.AltitudePerSlot[slotIdx];
            double moonSep = slotIdx < prof.MoonSepPerSlot.Length ? prof.MoonSepPerSlot[slotIdx] : 0;

            bool altPass = alt >= c.MinTargetAltitude;
            checks.Add(new ConstraintCheckModel {
                Icon = altPass ? "✓" : "✗", IconColor = altPass ? PassBrush : FailBrush,
                Label = "Altitude",
                Detail = $"Peak {alt:F0}° {(altPass ? "≥" : "<")} {c.MinTargetAltitude:F0}° min",
                DetailColor = DimBrush,
            });

            // Custom horizon (.hrz from the NINA profile) — at this slot's azimuth.
            // Same obstruction math the engine uses for SlotUsable.
            if (_customHorizon != null) {
                double az = AstroCalculator.TargetAzimuthAtTime(
                    slot.UtcStart, prof.Target.RaHours, prof.Target.DecDegrees, _latitude, _longitude);
                double hrzAlt = _customHorizon.AltitudeAt(az);
                bool hrzPass = alt >= hrzAlt;
                double clearance = alt - hrzAlt;
                checks.Add(new ConstraintCheckModel {
                    Icon = hrzPass ? "✓" : "✗", IconColor = hrzPass ? PassBrush : FailBrush,
                    Label = "Custom Horizon",
                    Detail = hrzPass
                        ? $"Clears {hrzAlt:F0}° horizon by {clearance:F0}° (az {az:F0}°)"
                        : $"{-clearance:F0}° below {hrzAlt:F0}° horizon (az {az:F0}°)",
                    DetailColor = DimBrush,
                });
            }

            double allocHrs = prof.AllocatedSec / 3600.0;
            bool timePass = c.MinTimeOnTargetHrs <= 0 || allocHrs >= c.MinTimeOnTargetHrs;
            checks.Add(new ConstraintCheckModel {
                Icon = timePass ? "✓" : "✗", IconColor = timePass ? PassBrush : FailBrush,
                Label = "Time",
                Detail = c.MinTimeOnTargetHrs > 0
                    ? $"{allocHrs:F1}h {(timePass ? "≥" : "<")} {c.MinTimeOnTargetHrs:F1}h min"
                    : $"{allocHrs:F1}h (no min set)",
                DetailColor = DimBrush,
            });

            bool hasLunar = prof.Target.Panels.SelectMany(p => p.ExposureSets).Any(es => es.HasMoonAvoidance && es.Remaining > 0);
            bool moonDown = slot.MoonAltDeg <= 0;
            bool moonPass;
            string moonDetail;
            if (!hasLunar) {
                moonPass = true;
                moonDetail = "No lunar-avoid filters";
            } else {
                moonPass = moonDown || prof.SlotMoonOk[slotIdx];
                moonDetail = moonPass
                    ? $"{(moonDown ? "Moon down" : "Safe")}, sep {moonSep:F0}°"
                    : $"Moon up, sep {moonSep:F0}°";
            }
            checks.Add(new ConstraintCheckModel {
                Icon = moonPass ? "✓" : "✗", IconColor = moonPass ? PassBrush : FailBrush,
                Label = "Moon", Detail = moonDetail, DetailColor = DimBrush,
            });

            string twilightName = c.SunAltitudeThreshold switch {
                >= -6 => "Civil", >= -12 => "Nautical", _ => "Astronomical",
            };
            bool darkPass = slot.SunAltDeg <= c.SunAltitudeThreshold;
            checks.Add(new ConstraintCheckModel {
                Icon = darkPass ? "✓" : "✗", IconColor = darkPass ? PassBrush : FailBrush,
                Label = "Darkness",
                Detail = darkPass
                    ? $"{twilightName} — Sun {slot.SunAltDeg:F0}°"
                    : $"Sun {slot.SunAltDeg:F0}° > {c.SunAltitudeThreshold:F0}° {twilightName}",
                DetailColor = DimBrush,
            });

            return checks;
        }

        // ── Log Building ──

        private void BuildLogEntries() {
            var entries = new ObservableCollection<SimLogEntry>();
            if (_log != null) {
                foreach (var e in _log)
                    entries.Add(e);
            }
            LogEntries = entries;
        }

        private void UpdateSummary() {
            if (_slots == null || _profiles == null) { SummaryText = ""; return; }
            double darkSlots = _slots.Count(s => s.SunAltDeg < -18);
            double darkHrs = darkSlots * 5.0 / 60.0;
            double moonIllum = _slots.Count > 0
                ? AstroCalculator.MoonIllumination(_slots[_slots.Count / 2].UtcStart) : 0;
            int targetCount = _profiles.Count(p => p.AllocatedSec > 0);
            int totalSubs = _log?.Count(e => e.Command == "Image" || e.Command == "Bonus") ?? 0;
            SummaryText = $"{darkHrs:F1} dark hrs  ·  {targetCount} target{(targetCount != 1 ? "s" : "")}  ·  {totalSubs} subs  ·  Moon {moonIllum:F0}%";
        }

        // ── INotifyPropertyChanged ──

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null) {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
