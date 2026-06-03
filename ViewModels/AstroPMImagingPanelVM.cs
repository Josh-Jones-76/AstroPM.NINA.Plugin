using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using global::NINA.WPF.Base.ViewModel;
using AstroPM.NINA.Plugin.Instructions;
using AstroPM.NINA.Plugin.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace AstroPM.NINA.Plugin.ViewModels {

    [Export(typeof(global::NINA.Equipment.Interfaces.ViewModel.IDockableVM))]
    public class AstroPMImagingPanelVM : DockableVM {

        private DispatcherTimer _pollTimer;
        private TargetInstructionSet _boundInstance;
        private bool _chartDataSent;

        [ImportingConstructor]
        public AstroPMImagingPanelVM(IProfileService profileService) : base(profileService) {
            Title = "Astro PM";

            var geo = new GeometryGroup();
            geo.FillRule = FillRule.EvenOdd;
            geo.Children.Add(Geometry.Parse("M1,0.5 L2.5,0.5 L2.5,9 L23,9 L23,10.5 L1,10.5 Z"));
            geo.Children.Add(Geometry.Parse("M3,10 C6,7 9,2 12,1.5 C15,2 18,7 22,10 Z"));
            geo.Children.Add(Geometry.Parse("M0.5,12 L23.5,12 L23.5,23.5 L0.5,23.5 Z M2,13.5 L22,13.5 L22,22 L2,22 Z"));
            geo.Children.Add(Geometry.Parse("M3,20.5 L5.2,14 L6.7,14 L8.9,20.5 L7.6,20.5 L7,18.5 L5.4,18.5 L4.8,20.5 Z M5.7,17.2 L6.2,15.2 L6.7,17.2 Z"));
            geo.Children.Add(Geometry.Parse("M9.8,14 L13.2,14 Q15,14 15,16.2 Q15,18 13.2,18 L11.3,18 L11.3,20.5 L9.8,20.5 Z M11.3,15.3 L12.8,15.3 Q13.5,15.3 13.5,16.2 Q13.5,16.8 12.8,16.8 L11.3,16.8 Z"));
            geo.Children.Add(Geometry.Parse("M16,14 L17.3,14 L18.8,17.5 L20.3,14 L21.6,14 L21.6,20.5 L20.3,20.5 L20.3,16.5 L18.8,19.5 L18.8,19.5 L17.3,16.5 L17.3,20.5 L16,20.5 Z"));
            geo.Freeze();
            ImageGeometry = geo;

            // Log all SVG resource keys available in NINA (scan merged dictionaries too)
            try {
                var keys = new System.Collections.Generic.List<string>();
                void ScanDict(ResourceDictionary rd) {
                    foreach (var key in rd.Keys) {
                        var keyStr = key?.ToString() ?? "";
                        if (keyStr.Contains("SVG", StringComparison.OrdinalIgnoreCase))
                            keys.Add(keyStr);
                    }
                    foreach (var merged in rd.MergedDictionaries)
                        ScanDict(merged);
                }
                ScanDict(Application.Current.Resources);
                keys.Sort();
                global::NINA.Core.Utility.Logger.Info($"AstroPM Panel | Available SVG keys ({keys.Count}): {string.Join(", ", keys)}");
            } catch (Exception ex) {
                global::NINA.Core.Utility.Logger.Warning($"AstroPM Panel | SVG scan error: {ex.Message}");
            }

            _pollTimer = new DispatcherTimer(DispatcherPriority.Background, Application.Current.Dispatcher) {
                Interval = TimeSpan.FromSeconds(2)
            };
            _pollTimer.Tick += Poll;
            _pollTimer.Start();
        }

        public override bool IsTool => true;

        private string _statusCommand = "";
        public string StatusCommand {
            get => _statusCommand;
            set { _statusCommand = value; RaisePropertyChanged(); }
        }

        private string _statusTarget = "";
        public string StatusTarget {
            get => _statusTarget;
            set { _statusTarget = value; RaisePropertyChanged(); }
        }

        private string _statusFilter = "";
        public string StatusFilter {
            get => _statusFilter;
            set { _statusFilter = value; RaisePropertyChanged(); }
        }

        private string _statusExposure = "";
        public string StatusExposure {
            get => _statusExposure;
            set { _statusExposure = value; RaisePropertyChanged(); }
        }

        private string _statusSub = "";
        public string StatusSub {
            get => _statusSub;
            set { _statusSub = value; RaisePropertyChanged(); }
        }

        private string _statusGainOffset = "";
        public string StatusGainOffset {
            get => _statusGainOffset;
            set { _statusGainOffset = value; RaisePropertyChanged(); }
        }

        private bool _hasStatus;
        public bool HasStatus {
            get => _hasStatus;
            set { _hasStatus = value; RaisePropertyChanged(); }
        }

        private bool _hasChartData;
        public bool HasChartData {
            get => _hasChartData;
            set { _hasChartData = value; RaisePropertyChanged(); }
        }

        public List<TimeSlot> ChartSlots { get; private set; }
        public List<TargetProfile> ChartProfiles { get; private set; }
        public List<SimLogEntry> ChartLog { get; private set; }

        // Set by the view code-behind once the chart control is loaded.
        // May be re-set if NINA recreates the panel.
        private Action<List<TimeSlot>, List<TargetProfile>, List<SimLogEntry>> _pushChartData;
        public Action<List<TimeSlot>, List<TargetProfile>, List<SimLogEntry>> PushChartData {
            get => _pushChartData;
            set { _pushChartData = value; _chartDataSent = false; }
        }

        private int _pollCount;
        private void Poll(object sender, EventArgs e) {
            _pollCount++;
            var instance = TargetInstructionSet.ActiveInstance;
            if (_pollCount == 1) {
                global::NINA.Core.Utility.Logger.Info("AstroPM Panel | Polling started, waiting for active instance");
            }
            if (instance == null) return;

            if (instance != _boundInstance) {
                if (_boundInstance != null)
                    _boundInstance.PropertyChanged -= OnInstancePropertyChanged;

                _boundInstance = instance;
                _boundInstance.PropertyChanged += OnInstancePropertyChanged;
                _chartDataSent = false;
                global::NINA.Core.Utility.Logger.Info(
                    $"AstroPM Panel | Bound to TargetInstructionSet (HasChartData={instance.HasChartData}, PushChartData={PushChartData != null})");
                SyncStatus();
            }

            // Push chart data when available and the chart callback is wired up
            if (_boundInstance.HasChartData
                && _boundInstance.LastSlots != null
                && _boundInstance.LastProfiles != null
                && _boundInstance.LastLog != null) {

                // Detect if the schedule was rebuilt (new list references) — e.g. new night
                bool dataChanged = _boundInstance.LastSlots != ChartSlots;
                if (!HasChartData || dataChanged) {
                    if (dataChanged)
                        global::NINA.Core.Utility.Logger.Info("AstroPM Panel | Schedule data changed, refreshing chart");
                    ChartSlots = _boundInstance.LastSlots;
                    ChartProfiles = _boundInstance.LastProfiles;
                    ChartLog = _boundInstance.LastLog;
                    HasChartData = true;
                    _chartDataSent = false;
                }

                if (!_chartDataSent && PushChartData != null) {
                    global::NINA.Core.Utility.Logger.Info(
                        $"AstroPM Panel | Pushing chart data: {ChartLog.Count} entries");
                    PushChartData.Invoke(ChartSlots, ChartProfiles, ChartLog);
                    _chartDataSent = true;
                }
            }
        }

        private void OnInstancePropertyChanged(object sender, PropertyChangedEventArgs e) {
            switch (e.PropertyName) {
                case nameof(TargetInstructionSet.LiveCommand):
                case nameof(TargetInstructionSet.LiveTarget):
                case nameof(TargetInstructionSet.LiveFilter):
                case nameof(TargetInstructionSet.LiveExposure):
                case nameof(TargetInstructionSet.LiveSub):
                case nameof(TargetInstructionSet.LiveGainOffset):
                case nameof(TargetInstructionSet.HasLiveStatus):
                    SyncStatus();
                    break;
                case nameof(TargetInstructionSet.HasChartData):
                    _chartDataSent = false;
                    break;
            }
        }

        private void SyncStatus() {
            if (_boundInstance == null) return;
            Application.Current?.Dispatcher?.Invoke(() => {
                StatusCommand = _boundInstance.LiveCommand;
                StatusTarget = _boundInstance.LiveTarget;
                StatusFilter = _boundInstance.LiveFilter;
                StatusExposure = _boundInstance.LiveExposure;
                StatusSub = _boundInstance.LiveSub;
                StatusGainOffset = _boundInstance.LiveGainOffset;
                HasStatus = _boundInstance.HasLiveStatus;
            });
        }
    }
}
