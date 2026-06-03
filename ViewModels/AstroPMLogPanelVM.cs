using NINA.Profile.Interfaces;
using global::NINA.WPF.Base.ViewModel;
using AstroPM.NINA.Plugin.Instructions;
using AstroPM.NINA.Plugin.Models;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace AstroPM.NINA.Plugin.ViewModels {

    [Export(typeof(global::NINA.Equipment.Interfaces.ViewModel.IDockableVM))]
    public class AstroPMLogPanelVM : DockableVM {

        private DispatcherTimer _pollTimer;
        private TargetInstructionSet _boundInstance;

        [ImportingConstructor]
        public AstroPMLogPanelVM(IProfileService profileService) : base(profileService) {
            Title = "Astro PM Log";

            var geo = new GeometryGroup();
            geo.FillRule = FillRule.EvenOdd;
            // List items above
            geo.Children.Add(Geometry.Parse("M1,1 L2.5,1 L2.5,2.5 L1,2.5 Z"));
            geo.Children.Add(Geometry.Parse("M4,1 L15,1 L15,2.5 L4,2.5 Z"));
            geo.Children.Add(Geometry.Parse("M1,4 L2.5,4 L2.5,5.5 L1,5.5 Z"));
            geo.Children.Add(Geometry.Parse("M4,4 L12,4 L12,5.5 L4,5.5 Z"));
            geo.Children.Add(Geometry.Parse("M1,7 L2.5,7 L2.5,8.5 L1,8.5 Z"));
            geo.Children.Add(Geometry.Parse("M4,7 L17,7 L17,8.5 L4,8.5 Z"));
            // Box with APM
            geo.Children.Add(Geometry.Parse("M0.5,11 L23.5,11 L23.5,23.5 L0.5,23.5 Z M2,12.5 L22,12.5 L22,22 L2,22 Z"));
            geo.Children.Add(Geometry.Parse("M3,20.5 L5.2,13 L6.7,13 L8.9,20.5 L7.6,20.5 L7,18.5 L5.4,18.5 L4.8,20.5 Z M5.7,17.2 L6.2,14.5 L6.7,17.2 Z"));
            geo.Children.Add(Geometry.Parse("M9.8,13 L13.2,13 Q15,13 15,15.2 Q15,17 13.2,17 L11.3,17 L11.3,20.5 L9.8,20.5 Z M11.3,14.3 L12.8,14.3 Q13.5,14.3 13.5,15.2 Q13.5,15.8 12.8,15.8 L11.3,15.8 Z"));
            geo.Children.Add(Geometry.Parse("M16,13 L17.3,13 L18.8,16.5 L20.3,13 L21.6,13 L21.6,20.5 L20.3,20.5 L20.3,16 L18.8,19 L18.8,19 L17.3,16 L17.3,20.5 L16,20.5 Z"));
            geo.Freeze();
            ImageGeometry = geo;

            LogEntries = new ObservableCollection<SimLogEntry>();

            _pollTimer = new DispatcherTimer(DispatcherPriority.Background, Application.Current.Dispatcher) {
                Interval = TimeSpan.FromSeconds(2)
            };
            _pollTimer.Tick += Poll;
            _pollTimer.Start();
        }

        public override bool IsTool => true;

        public ObservableCollection<SimLogEntry> LogEntries { get; }

        private SimLogEntry _activeLogEntry;
        public SimLogEntry ActiveLogEntry {
            get => _activeLogEntry;
            set { _activeLogEntry = value; RaisePropertyChanged(); }
        }

        private bool _autoScrollLog = true;
        public bool AutoScrollLog {
            get => _autoScrollLog;
            set { _autoScrollLog = value; RaisePropertyChanged(); }
        }

        private bool _hasLogData;
        public bool HasLogData {
            get => _hasLogData;
            set { _hasLogData = value; RaisePropertyChanged(); }
        }

        private void Poll(object sender, EventArgs e) {
            var instance = TargetInstructionSet.ActiveInstance;
            if (instance == null) return;

            if (instance != _boundInstance) {
                if (_boundInstance != null)
                    _boundInstance.PropertyChanged -= OnInstancePropertyChanged;

                _boundInstance = instance;
                _boundInstance.PropertyChanged += OnInstancePropertyChanged;
                SyncLog();
                SyncActiveEntry();
            }
        }

        private void OnInstancePropertyChanged(object sender, PropertyChangedEventArgs e) {
            switch (e.PropertyName) {
                case nameof(TargetInstructionSet.HasChartData):
                    SyncLog();
                    break;
                case nameof(TargetInstructionSet.ActiveLogEntry):
                    SyncActiveEntry();
                    break;
            }
        }

        private void SyncLog() {
            if (_boundInstance == null || !_boundInstance.HasChartData || _boundInstance.LastLog == null) return;

            Application.Current?.Dispatcher?.Invoke(() => {
                LogEntries.Clear();
                foreach (var entry in _boundInstance.LastLog)
                    LogEntries.Add(entry);
                HasLogData = LogEntries.Count > 0;
            });
        }

        private void SyncActiveEntry() {
            if (_boundInstance == null) return;
            Application.Current?.Dispatcher?.Invoke(() => {
                ActiveLogEntry = _boundInstance.ActiveLogEntry;
            });
        }
    }
}
