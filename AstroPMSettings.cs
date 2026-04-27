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
