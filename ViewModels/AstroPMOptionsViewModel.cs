using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AstroPM.NINA.Plugin.Models;
using AstroPM.NINA.Plugin.Services;

namespace AstroPM.NINA.Plugin.ViewModels
{
    public class AstroPMOptionsViewModel : INotifyPropertyChanged
    {
        /// <summary>
        /// Fired whenever the filtered target list changes. FramingInjector subscribes to this.
        /// </summary>
        public static event Action<List<ProjectTarget>> FilteredTargetsChanged;

        private readonly AstroPMSettings _settings;
        private readonly AstroPMApiService _apiService;
        private static readonly object _targetsLock = new object();

        private string _syncToken;
        private bool _autoRefreshOnOpen;
        private string _statusMessage;
        private Brush _statusColor;
        private bool _isConnected;
        private bool _isTesting;
        private ObservableCollection<ProjectTarget> _targets;
        private List<ProjectTarget> _allTargets = new List<ProjectTarget>();
        private ProjectTarget _selectedTarget;
        private string _loadResultMessage;

        // Filters
        private string _statusFilter;
        private string _locationFilter;
        private string _telescopeFilter;
        private string _cameraFilter;
        private ObservableCollection<string> _availableStatuses = new ObservableCollection<string>();
        private ObservableCollection<string> _availableLocations = new ObservableCollection<string>();
        private ObservableCollection<string> _availableTelescopes = new ObservableCollection<string>();
        private ObservableCollection<string> _availableCameras = new ObservableCollection<string>();

        public AstroPMOptionsViewModel()
        {
            _settings = AstroPMSettings.Load();
            _apiService = new AstroPMApiService();

            _syncToken = _settings.SyncToken;
            _statusFilter = _settings.StatusFilter;
            _locationFilter = _settings.LocationFilter;
            _telescopeFilter = _settings.TelescopeFilter;
            _cameraFilter = _settings.CameraFilter;
            _autoRefreshOnOpen = _settings.AutoRefreshOnOpen;

            _targets = new ObservableCollection<ProjectTarget>();
            System.Windows.Data.BindingOperations.EnableCollectionSynchronization(_targets, _targetsLock);

            SaveAndConnectCommand = new RelayCommand(async _ => await SaveAndConnectAsync(), _ => !IsTesting);
            RefreshCommand = new RelayCommand(async _ => await FetchTargetsAsync(), _ => IsConnected && !IsTesting);
            LoadToFramingCommand = new RelayCommand(_ => { LoadSelectedToFraming(); return Task.CompletedTask; }, _ => SelectedTarget != null);
            ClearFiltersCommand = new RelayCommand(_ => { ClearFilters(); return Task.CompletedTask; });

            if (!string.IsNullOrWhiteSpace(_syncToken))
            {
                _ = SaveAndConnectAsync();
            }
        }

        // ── Properties ──

        public string SyncToken
        {
            get => _syncToken;
            set { _syncToken = value; OnPropertyChanged(); }
        }

        public bool AutoRefreshOnOpen
        {
            get => _autoRefreshOnOpen;
            set { _autoRefreshOnOpen = value; OnPropertyChanged(); }
        }

        // ── Filter Properties ──

        public string StatusFilter
        {
            get => _statusFilter;
            set { _statusFilter = value; OnPropertyChanged(); ApplyFilters(); }
        }

        public string LocationFilter
        {
            get => _locationFilter;
            set { _locationFilter = value; OnPropertyChanged(); ApplyFilters(); }
        }

        public string TelescopeFilter
        {
            get => _telescopeFilter;
            set { _telescopeFilter = value; OnPropertyChanged(); ApplyFilters(); }
        }

        public string CameraFilter
        {
            get => _cameraFilter;
            set { _cameraFilter = value; OnPropertyChanged(); ApplyFilters(); }
        }

        public ObservableCollection<string> AvailableStatuses
        {
            get => _availableStatuses;
            set { _availableStatuses = value; OnPropertyChanged(); }
        }

        public ObservableCollection<string> AvailableLocations
        {
            get => _availableLocations;
            set { _availableLocations = value; OnPropertyChanged(); }
        }

        public ObservableCollection<string> AvailableTelescopes
        {
            get => _availableTelescopes;
            set { _availableTelescopes = value; OnPropertyChanged(); }
        }

        public ObservableCollection<string> AvailableCameras
        {
            get => _availableCameras;
            set { _availableCameras = value; OnPropertyChanged(); }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set { _statusMessage = value; OnPropertyChanged(); }
        }

        public Brush StatusColor
        {
            get => _statusColor;
            set { _statusColor = value; OnPropertyChanged(); }
        }

        public bool IsConnected
        {
            get => _isConnected;
            set { _isConnected = value; OnPropertyChanged(); (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged(); }
        }

        public bool IsTesting
        {
            get => _isTesting;
            set
            {
                _isTesting = value;
                OnPropertyChanged();
                (SaveAndConnectCommand as RelayCommand)?.RaiseCanExecuteChanged();
                (RefreshCommand as RelayCommand)?.RaiseCanExecuteChanged();
            }
        }

        public ObservableCollection<ProjectTarget> Targets
        {
            get => _targets;
            set { _targets = value; OnPropertyChanged(); }
        }

        public ProjectTarget SelectedTarget
        {
            get => _selectedTarget;
            set
            {
                _selectedTarget = value;
                OnPropertyChanged();
                (LoadToFramingCommand as RelayCommand)?.RaiseCanExecuteChanged();
                LoadResultMessage = null;
            }
        }

        public string LoadResultMessage
        {
            get => _loadResultMessage;
            set { _loadResultMessage = value; OnPropertyChanged(); }
        }

        // ── Commands ──

        public ICommand SaveAndConnectCommand { get; }
        public ICommand RefreshCommand { get; }
        public ICommand LoadToFramingCommand { get; }
        public ICommand ClearFiltersCommand { get; }

        // ── Actions ──

        private async Task SaveAndConnectAsync()
        {
            if (string.IsNullOrWhiteSpace(SyncToken))
            {
                SetStatus("Please enter a sync token.", StatusColors.Amber);
                IsConnected = false;
                return;
            }

            _settings.SyncToken = SyncToken;
            _settings.StatusFilter = StatusFilter;
            _settings.LocationFilter = LocationFilter;
            _settings.TelescopeFilter = TelescopeFilter;
            _settings.CameraFilter = CameraFilter;
            _settings.AutoRefreshOnOpen = AutoRefreshOnOpen;
            _settings.Save();

            await FetchTargetsAsync();
        }

        private async Task FetchTargetsAsync()
        {
            if (string.IsNullOrWhiteSpace(SyncToken))
            {
                SetStatus("No sync token.", StatusColors.Amber);
                return;
            }

            IsTesting = true;
            SetStatus("Connecting...", StatusColors.Gray);

            try
            {
                // Fetch all targets (no server-side status filter — we filter client-side)
                var result = await _apiService.ListTargetsAsync(SyncToken, null);

                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    if (result.Success && result.Targets != null)
                    {
                        _allTargets = result.Targets;
                        RebuildFilterOptions();
                        ApplyFilters();
                        IsConnected = true;
                        SetStatus($"Connected — {result.Targets.Count} target(s) found.", StatusColors.Green);
                    }
                    else
                    {
                        _allTargets.Clear();
                        _targets.Clear();
                        IsConnected = false;
                        SetStatus(result.Message ?? "Failed to connect.", StatusColors.Red);
                    }
                });
            }
            catch (Exception ex)
            {
                IsConnected = false;
                SetStatus($"Error: {ex.Message}", StatusColors.Red);
                Application.Current?.Dispatcher?.Invoke(() => { _allTargets.Clear(); _targets.Clear(); });
            }
            finally
            {
                IsTesting = false;
            }
        }

        private void RebuildFilterOptions()
        {
            AvailableStatuses = new ObservableCollection<string>(
                _allTargets.Select(t => t.Status).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(s => s));
            AvailableLocations = new ObservableCollection<string>(
                _allTargets.Select(t => t.LocationName).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(s => s));
            AvailableTelescopes = new ObservableCollection<string>(
                _allTargets.Select(t => t.TelescopeName).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(s => s));
            AvailableCameras = new ObservableCollection<string>(
                _allTargets.Select(t => t.CameraName).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderBy(s => s));
        }

        private void ApplyFilters()
        {
            var filtered = _allTargets.AsEnumerable();

            if (!string.IsNullOrEmpty(StatusFilter))
                filtered = filtered.Where(t => string.Equals(t.Status, StatusFilter, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(LocationFilter))
                filtered = filtered.Where(t => string.Equals(t.LocationName, LocationFilter, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(TelescopeFilter))
                filtered = filtered.Where(t => string.Equals(t.TelescopeName, TelescopeFilter, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(CameraFilter))
                filtered = filtered.Where(t => string.Equals(t.CameraName, CameraFilter, StringComparison.OrdinalIgnoreCase));

            var list = filtered.ToList();

            _targets.Clear();
            foreach (var t in list)
                _targets.Add(t);

            // Save filters to settings
            _settings.StatusFilter = StatusFilter;
            _settings.LocationFilter = LocationFilter;
            _settings.TelescopeFilter = TelescopeFilter;
            _settings.CameraFilter = CameraFilter;
            _settings.Save();

            // Notify FramingInjector of the updated list
            FilteredTargetsChanged?.Invoke(list);
        }

        private void ClearFilters()
        {
            _statusFilter = null; OnPropertyChanged(nameof(StatusFilter));
            _locationFilter = null; OnPropertyChanged(nameof(LocationFilter));
            _telescopeFilter = null; OnPropertyChanged(nameof(TelescopeFilter));
            _cameraFilter = null; OnPropertyChanged(nameof(CameraFilter));
            ApplyFilters();
        }

        private void LoadSelectedToFraming()
        {
            var target = SelectedTarget;
            if (target == null) return;

            try
            {
                var framingVM = FindFramingAssistantVM();
                if (framingVM != null)
                {
                    // Navigate FIRST so the Framing tab visual tree renders
                    NavigateToFramingTab();
                    SetFramingCoordinates(framingVM, target);
                    // Kick off async: wait for tab render, load image, then reapply
                    _ = LoadAndReapplyAsync(framingVM, target);
                    LoadResultMessage = $"Loaded '{target.TargetName}' to Framing Assistant.";
                }
                else
                {
                    LoadResultMessage = "Could not find the Framing Assistant.";
                }
            }
            catch (Exception ex)
            {
                LoadResultMessage = $"Error: {ex.Message}";
            }
        }

        private async Task LoadAndReapplyAsync(object framingVM, ProjectTarget target)
        {
            // Give the Framing tab time to render its visual tree
            await Task.Delay(1500);

            // Reset panels to 1x1 via UI controls (not just reflection) before loading
            // This ensures NINA's recalculation uses 1x1 during LoadImage
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var mainWindow = Application.Current.MainWindow;

                    // Reset rotation slider to 0
                    var rotSlider = FramingInjector.FindRotationSlider(mainWindow);
                    if (rotSlider != null) rotSlider.Value = 0;

                    // Reset horizontal panels to 1
                    var hCtrl = FramingInjector.FindTextBoxNearLabel(mainWindow, "Horizontal panels");
                    if (hCtrl != null)
                    {
                        hCtrl.Text = "1";
                        hCtrl.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    }

                    // Reset vertical panels to 1
                    var vCtrl = FramingInjector.FindTextBoxNearLabel(mainWindow, "Vertical panels");
                    if (vCtrl != null)
                    {
                        vCtrl.Text = "1";
                        vCtrl.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    }
                }
                catch { }
            });

            await Task.Delay(300);

            // Now execute LoadImage on the UI thread
            await Application.Current.Dispatcher.InvokeAsync(() => ExecuteLoadImage(framingVM));

            // Reapply rotation/panels/overlap after image loads
            await ReapplyPanelsAfterDelay(framingVM, target);
        }

        private object FindFramingAssistantVM()
        {
            try
            {
                var app = Application.Current;
                if (app?.MainWindow?.DataContext == null) return null;

                var mainDC = app.MainWindow.DataContext;
                var dcType = mainDC.GetType();

                // NINA's ApplicationVM has a FramingAssistantVM property
                var framingProp = dcType.GetProperty("FramingAssistantVM");
                if (framingProp != null)
                    return framingProp.GetValue(mainDC);
            }
            catch { }

            return null;
        }

        private void SetFramingCoordinates(object framingVM, ProjectTarget target)
        {
            var vmType = framingVM.GetType();

            // Target Name — set via DeepSkyObjectSearchVM.SetTargetNameWithoutSearch
            try
            {
                var searchVmProp = vmType.GetProperty("DeepSkyObjectSearchVM");
                if (searchVmProp != null)
                {
                    var searchVM = searchVmProp.GetValue(framingVM);
                    if (searchVM != null)
                    {
                        var setName = searchVM.GetType().GetMethod("SetTargetNameWithoutSearch");
                        if (setName != null)
                            setName.Invoke(searchVM, new object[] { target.TargetName });
                        else
                            searchVM.GetType().GetProperty("TargetName")?.SetValue(searchVM, target.TargetName);
                    }
                }
            }
            catch { }

            // RA/Dec — bypass NINA's H/M/S component setters (they cross-reference
            // live Coordinates values, so leftover fractions from a prior target
            // pollute the math). Replace DSO.Coordinates with a fresh instance.
            FramingInjector.SetCoordinatesDirect(framingVM, target.RaHours, target.DecDegrees);

            // Camera parameters
            if (target.CameraPixelWidth.HasValue)
                vmType.GetProperty("CameraWidth")?.SetValue(framingVM, target.CameraPixelWidth.Value);
            if (target.CameraPixelHeight.HasValue)
                vmType.GetProperty("CameraHeight")?.SetValue(framingVM, target.CameraPixelHeight.Value);
            if (target.CameraPixelSizeUm.HasValue)
                vmType.GetProperty("CameraPixelSize")?.SetValue(framingVM, target.CameraPixelSizeUm.Value);

            // Focal length
            if (target.TelescopeFocalLengthMm.HasValue)
                vmType.GetProperty("FocalLength")?.SetValue(framingVM, target.TelescopeFocalLengthMm.Value);

            // Reset rotation to 0 and panels to 1x1 before loading image
            // Real values applied in ReapplyPanelsAfterDelay after image loads
            vmType.GetProperty("Rotation")?.SetValue(framingVM, 0.0);
            vmType.GetProperty("HorizontalPanels")?.SetValue(framingVM, 1);
            vmType.GetProperty("VerticalPanels")?.SetValue(framingVM, 1);
        }

        private void ExecuteLoadImage(object framingVM)
        {
            try
            {
                var vmType = framingVM.GetType();
                var loadCmd = vmType.GetProperty("LoadImageCommand")?.GetValue(framingVM);
                if (loadCmd != null)
                {
                    var execMethod = loadCmd.GetType().GetMethod("Execute", new[] { typeof(object) });
                    execMethod?.Invoke(loadCmd, new object[] { null });
                }
            }
            catch { }
        }

        private async Task ReapplyPanelsAfterDelay(object framingVM, ProjectTarget target)
        {
            // Poll until the panel UI controls are available (image loaded & visual tree rendered)
            // NINA hides the mosaic section while downloading the sky survey image
            for (int i = 0; i < 30; i++) // up to 30 seconds
            {
                await Task.Delay(1000);
                bool found = false;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var mw = Application.Current.MainWindow;
                    var h = FramingInjector.FindTextBoxNearLabel(mw, "Horizontal panels");
                    var v = FramingInjector.FindTextBoxNearLabel(mw, "Vertical panels");
                    found = h != null && v != null;
                });
                if (found) break;
            }

            // Wait for NINA's post-load recalculations to settle
            await Task.Delay(3000);

            // Apply all values, then re-apply panels at the end
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var mainWindow = Application.Current.MainWindow;

                    // Rotation via slider
                    var rotationSlider = FramingInjector.FindRotationSlider(mainWindow);
                    if (rotationSlider != null)
                        rotationSlider.Value = target.RotationDeg;

                    // Panels first pass
                    var hCtrl = FramingInjector.FindTextBoxNearLabel(mainWindow, "Horizontal panels");
                    var vCtrl = FramingInjector.FindTextBoxNearLabel(mainWindow, "Vertical panels");
                    if (target.PanelColumns > 0 && hCtrl != null)
                    {
                        hCtrl.Text = target.PanelColumns.ToString();
                        hCtrl.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    }
                    if (target.PanelRows > 0 && vCtrl != null)
                    {
                        vCtrl.Text = target.PanelRows.ToString();
                        vCtrl.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    }
                }
                catch { }
            });

            await Task.Delay(500);

            // Overlap nudge
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var mainWindow = Application.Current.MainWindow;
                    var overlapControl = FramingInjector.FindOverlapControl(mainWindow);
                    if (overlapControl != null)
                    {
                        double overlap = target.PanelOverlapPercent > 0 ? target.PanelOverlapPercent : 20.0;
                        overlapControl.Text = (overlap + 1).ToString("F1");
                        overlapControl.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    }
                }
                catch { }
            });

            await Task.Delay(500);

            // Final overlap + re-apply panels (in case overlap nudge reset them)
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var mainWindow = Application.Current.MainWindow;

                    var overlapControl = FramingInjector.FindOverlapControl(mainWindow);
                    if (overlapControl != null)
                    {
                        double overlap = target.PanelOverlapPercent > 0 ? target.PanelOverlapPercent : 20.0;
                        overlapControl.Text = overlap.ToString("F1");
                        overlapControl.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    }

                    var hCtrl = FramingInjector.FindTextBoxNearLabel(mainWindow, "Horizontal panels");
                    var vCtrl = FramingInjector.FindTextBoxNearLabel(mainWindow, "Vertical panels");
                    if (target.PanelColumns > 0 && hCtrl != null)
                    {
                        hCtrl.Text = target.PanelColumns.ToString();
                        hCtrl.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    }
                    if (target.PanelRows > 0 && vCtrl != null)
                    {
                        vCtrl.Text = target.PanelRows.ToString();
                        vCtrl.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    }
                }
                catch { }
            });
        }

        private void NavigateToFramingTab()
        {
            try
            {
                var app = Application.Current;
                if (app?.MainWindow?.DataContext == null) return;

                var mainDC = app.MainWindow.DataContext;
                var dcType = mainDC.GetType();

                // Try ChangeTab via any mediator-like property
                foreach (var prop in dcType.GetProperties())
                {
                    var changeTab = prop.PropertyType.GetMethod("ChangeTab");
                    if (changeTab != null)
                    {
                        var mediator = prop.GetValue(mainDC);
                        if (mediator != null)
                        {
                            var paramType = changeTab.GetParameters()[0].ParameterType;
                            var framingValue = Enum.Parse(paramType, "FRAMINGASSISTANT");
                            changeTab.Invoke(mediator, new[] { framingValue });
                            return;
                        }
                    }
                }

                // Fallback: set TabIndex directly (Framing is index 2 in NINA's left menu)
                var tabProp = dcType.GetProperty("TabIndex");
                if (tabProp != null && tabProp.CanWrite)
                {
                    tabProp.SetValue(mainDC, 2);
                }
            }
            catch { }
        }

        private void SetStatus(string message, SolidColorBrush color)
        {
            StatusMessage = message;
            StatusColor = color;
        }

        // ── INotifyPropertyChanged ──

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private static class StatusColors
        {
            public static readonly SolidColorBrush Green = new SolidColorBrush(Color.FromRgb(34, 197, 94));
            public static readonly SolidColorBrush Red = new SolidColorBrush(Color.FromRgb(239, 68, 68));
            public static readonly SolidColorBrush Amber = new SolidColorBrush(Color.FromRgb(245, 158, 11));
            public static readonly SolidColorBrush Gray = new SolidColorBrush(Color.FromRgb(156, 163, 175));
        }
    }
}
