using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace AstroPM.NINA.Plugin
{
    public class AstroPMSettings : INotifyPropertyChanged
    {
        private string _syncToken = string.Empty;
        private string _statusFilter = string.Empty;
        private string _locationFilter = string.Empty;
        private string _telescopeFilter = string.Empty;
        private string _cameraFilter = string.Empty;
        private bool _autoRefreshOnOpen = true;
        private string _simStatusFilter = "Active";
        private string _simLocationFilter = string.Empty;
        private string _simTelescopeFilter = string.Empty;
        private bool _ditherEnabled = true;
        private int _ditherEvery = 3;
        private bool _filterSwitchEnabled = true;
        private int _filterSwitchCount = 20;
        private bool _bonusEnabled = true;
        private bool _mosaicPanelPreference = true;
        private string _sortChain = "LowestPeakAltitude,SettingSoonest,MostRemainingWork,Constrained";

        public string SyncToken
        {
            get => _syncToken;
            set { if (_syncToken != value) { _syncToken = value; OnPropertyChanged(); } }
        }

        public string StatusFilter
        {
            get => _statusFilter;
            set { if (_statusFilter != value) { _statusFilter = value; OnPropertyChanged(); } }
        }

        public string LocationFilter
        {
            get => _locationFilter;
            set { if (_locationFilter != value) { _locationFilter = value; OnPropertyChanged(); } }
        }

        public string TelescopeFilter
        {
            get => _telescopeFilter;
            set { if (_telescopeFilter != value) { _telescopeFilter = value; OnPropertyChanged(); } }
        }

        public string CameraFilter
        {
            get => _cameraFilter;
            set { if (_cameraFilter != value) { _cameraFilter = value; OnPropertyChanged(); } }
        }

        public bool AutoRefreshOnOpen
        {
            get => _autoRefreshOnOpen;
            set { if (_autoRefreshOnOpen != value) { _autoRefreshOnOpen = value; OnPropertyChanged(); } }
        }

        public string SimStatusFilter
        {
            get => _simStatusFilter;
            set { if (_simStatusFilter != value) { _simStatusFilter = value; OnPropertyChanged(); } }
        }

        public string SimLocationFilter
        {
            get => _simLocationFilter;
            set { if (_simLocationFilter != value) { _simLocationFilter = value; OnPropertyChanged(); } }
        }

        public string SimTelescopeFilter
        {
            get => _simTelescopeFilter;
            set { if (_simTelescopeFilter != value) { _simTelescopeFilter = value; OnPropertyChanged(); } }
        }

        public bool DitherEnabled
        {
            get => _ditherEnabled;
            set { if (_ditherEnabled != value) { _ditherEnabled = value; OnPropertyChanged(); } }
        }

        public int DitherEvery
        {
            get => _ditherEvery;
            set { if (_ditherEvery != value) { _ditherEvery = value; OnPropertyChanged(); } }
        }

        public bool FilterSwitchEnabled
        {
            get => _filterSwitchEnabled;
            set { if (_filterSwitchEnabled != value) { _filterSwitchEnabled = value; OnPropertyChanged(); } }
        }

        public int FilterSwitchCount
        {
            get => _filterSwitchCount;
            set { if (_filterSwitchCount != value) { _filterSwitchCount = value; OnPropertyChanged(); } }
        }

        public bool BonusEnabled
        {
            get => _bonusEnabled;
            set { if (_bonusEnabled != value) { _bonusEnabled = value; OnPropertyChanged(); } }
        }

        public bool MosaicPanelPreference
        {
            get => _mosaicPanelPreference;
            set { if (_mosaicPanelPreference != value) { _mosaicPanelPreference = value; OnPropertyChanged(); } }
        }

        public string SortChain
        {
            get => _sortChain;
            set { if (_sortChain != value) { _sortChain = value; OnPropertyChanged(); } }
        }

        // ── Persistence ──

        private static string SettingsDir =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "Plugins", "AstroPM.NINA.Plugin");

        private static string SettingsFile => Path.Combine(SettingsDir, "settings.json");

        public static AstroPMSettings Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    var json = File.ReadAllText(SettingsFile);
                    return JsonConvert.DeserializeObject<AstroPMSettings>(json) ?? new AstroPMSettings();
                }
            }
            catch { }
            return new AstroPMSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(SettingsDir);
                var json = JsonConvert.SerializeObject(this, Formatting.Indented);
                File.WriteAllText(SettingsFile, json);
            }
            catch { }
        }

        // ── INotifyPropertyChanged ──

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
