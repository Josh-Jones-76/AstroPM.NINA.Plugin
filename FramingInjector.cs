using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AstroPM.NINA.Plugin.Models;
using AstroPM.NINA.Plugin.Services;
using AstroPM.NINA.Plugin.ViewModels;

namespace AstroPM.NINA.Plugin
{
    public class FramingInjector
    {
        private readonly AstroPMSettings _settings;
        private readonly AstroPMApiService _apiService;
        private readonly ObservableCollection<ProjectTarget> _targets = new();
        private bool _injected;
        private bool _stopped;
        private ComboBox _comboBox;

        public FramingInjector()
        {
            _settings = AstroPMSettings.Load();
            _apiService = new AstroPMApiService();

            // Subscribe to filter changes from Options page
            AstroPMOptionsViewModel.FilteredTargetsChanged += OnFilteredTargetsChanged;
        }

        private void OnFilteredTargetsChanged(List<ProjectTarget> filteredTargets)
        {
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                _targets.Clear();
                foreach (var t in filteredTargets)
                    _targets.Add(t);
            });
        }

        public void Start()
        {
            _stopped = false;
            Task.Run(async () =>
            {
                while (!_stopped && !_injected)
                {
                    await Task.Delay(2000);
                    try
                    {
                        Application.Current?.Dispatcher?.Invoke(() => TryInject());
                    }
                    catch { }
                }
            });
        }

        public void Stop()
        {
            _stopped = true;
        }

        private int _scanCount;

        private void TryInject()
        {
            if (_injected) return;
            _scanCount++;

            try
            {
                var mainWindow = Application.Current?.MainWindow;
                if (mainWindow == null) return;

                // Dump visual tree on 3rd scan for debugging
                if (_scanCount == 3)
                {
                }

                // Find the FramingAssistantView by type name
                var framingView = FindVisualChild(mainWindow, element =>
                    element.GetType().Name.Contains("FramingAssistant") &&
                    element is UserControl);

                if (framingView == null)
                {
                    // Try broader search - any element with "Framing" in type name
                    framingView = FindVisualChild(mainWindow, element =>
                        element.GetType().Name.Contains("Framing") &&
                        element is FrameworkElement fe && fe.ActualWidth > 100);
                }

                if (framingView == null) return;

                // Find the first ScrollViewer inside the framing view
                var scrollViewer = FindVisualChild(framingView, e => e is ScrollViewer) as ScrollViewer;
                if (scrollViewer == null) return;

                // The ScrollViewer's content should be a panel (StackPanel or Grid)
                var contentPanel = scrollViewer.Content as Panel;
                if (contentPanel == null)
                {
                    // Try looking inside the ScrollViewer's visual tree
                    contentPanel = FindVisualChild(scrollViewer, e =>
                        e is StackPanel sp && sp.Children.Count >= 2) as Panel;
                }

                if (contentPanel == null) return;

                // Build and insert our strip at the top of the panel
                var strip = BuildTargetStrip();

                if (contentPanel is StackPanel stackPanel)
                {
                    stackPanel.Children.Insert(0, strip);
                }
                else if (contentPanel is Grid grid)
                {
                    // Add a new row at the top
                    grid.RowDefinitions.Insert(0, new RowDefinition { Height = GridLength.Auto });
                    // Shift existing children down
                    foreach (UIElement child in grid.Children)
                    {
                        int row = Grid.GetRow(child);
                        Grid.SetRow(child, row + 1);
                    }
                    Grid.SetRow(strip, 0);
                    Grid.SetColumnSpan(strip, grid.ColumnDefinitions.Count > 0 ? grid.ColumnDefinitions.Count : 1);
                    grid.Children.Add(strip);
                }

                _injected = true;

                _ = LoadTargetsAsync();
            }
            catch { }
        }

        private FrameworkElement BuildTargetStrip()
        {
            // Main container
            var border = new Border
            {
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = Application.Current.TryFindResource("BorderBrush") as Brush ?? Brushes.Gray,
                Padding = new Thickness(4, 6, 4, 8),
                Margin = new Thickness(0, 0, 0, 4)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

            // Label
            var label = new TextBlock
            {
                Text = "AstroPM Targets",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = 13,
                Foreground = Application.Current.TryFindResource("PrimaryBrush") as Brush ?? Brushes.White
            };
            Grid.SetColumn(label, 0);
            grid.Children.Add(label);

            // ComboBox
            _comboBox = new ComboBox
            {
                ItemsSource = _targets,
                DisplayMemberPath = "TargetName",
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
                MinHeight = 26
            };
            // Try to apply NINA theme
            try
            {
                _comboBox.Background = Application.Current.TryFindResource("SecondaryBackgroundBrush") as Brush;
                _comboBox.Foreground = Application.Current.TryFindResource("PrimaryBrush") as Brush;
                _comboBox.BorderBrush = Application.Current.TryFindResource("BorderBrush") as Brush;
            }
            catch { }
            Grid.SetColumn(_comboBox, 1);
            grid.Children.Add(_comboBox);

            // Load button
            var loadBtn = new Button
            {
                Content = "Load",
                MinWidth = 50,
                MinHeight = 26,
                Margin = new Thickness(0, 0, 4, 0)
            };
            try { loadBtn.Style = Application.Current.TryFindResource("StandardButton") as Style; } catch { }
            loadBtn.Click += (s, e) => LoadSelectedTarget();
            Grid.SetColumn(loadBtn, 2);
            grid.Children.Add(loadBtn);

            // Refresh button
            var refreshBtn = new Button
            {
                Content = "\u21BB",
                MinWidth = 26,
                MinHeight = 26,
                ToolTip = "Refresh targets from Astro PM"
            };
            try { refreshBtn.Style = Application.Current.TryFindResource("StandardButton") as Style; } catch { }
            refreshBtn.Click += async (s, e) => await LoadTargetsAsync();
            Grid.SetColumn(refreshBtn, 3);
            grid.Children.Add(refreshBtn);

            border.Child = grid;
            return border;
        }

        private async Task LoadTargetsAsync()
        {
            // Re-read settings in case filters were changed
            var settings = AstroPMSettings.Load();
            var token = settings.SyncToken;
            if (string.IsNullOrWhiteSpace(token)) return;

            try
            {
                // Fetch all targets, then apply saved filters client-side
                var result = await _apiService.ListTargetsAsync(token, null);

                Application.Current?.Dispatcher?.Invoke(() =>
                {
                    _targets.Clear();
                    if (result.Success && result.Targets != null)
                    {
                        var filtered = result.Targets.AsEnumerable();

                        if (!string.IsNullOrEmpty(settings.StatusFilter))
                            filtered = filtered.Where(t => string.Equals(t.Status, settings.StatusFilter, StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrEmpty(settings.LocationFilter))
                            filtered = filtered.Where(t => string.Equals(t.LocationName, settings.LocationFilter, StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrEmpty(settings.TelescopeFilter))
                            filtered = filtered.Where(t => string.Equals(t.TelescopeName, settings.TelescopeFilter, StringComparison.OrdinalIgnoreCase));
                        if (!string.IsNullOrEmpty(settings.CameraFilter))
                            filtered = filtered.Where(t => string.Equals(t.CameraName, settings.CameraFilter, StringComparison.OrdinalIgnoreCase));

                        foreach (var t in filtered)
                            _targets.Add(t);
                    }
                });
            }
            catch { }
        }

        private void LoadSelectedTarget()
        {
            var target = _comboBox?.SelectedItem as ProjectTarget;
            if (target == null) return;

            try
            {
                var framingVM = FindFramingAssistantVM();
                if (framingVM == null) return;

                var vmType = framingVM.GetType();

                // Target Name
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

                // RA
                double totalRaH = target.RaHours;
                int raH = (int)totalRaH;
                double raRemain = (totalRaH - raH) * 60.0;
                int raM = (int)raRemain;
                double raS = (raRemain - raM) * 60.0;

                vmType.GetProperty("RAHours")?.SetValue(framingVM, raH);
                vmType.GetProperty("RAMinutes")?.SetValue(framingVM, raM);
                vmType.GetProperty("RASeconds")?.SetValue(framingVM, raS);

                // Dec
                double totalDec = target.DecDegrees;
                bool negative = totalDec < 0;
                totalDec = Math.Abs(totalDec);
                int decD = (int)totalDec;
                double decRemain = (totalDec - decD) * 60.0;
                int decM = (int)decRemain;
                double decS = (decRemain - decM) * 60.0;

                vmType.GetProperty("NegativeDec")?.SetValue(framingVM, negative);
                vmType.GetProperty("DecDegrees")?.SetValue(framingVM, decD);
                vmType.GetProperty("DecMinutes")?.SetValue(framingVM, decM);
                vmType.GetProperty("DecSeconds")?.SetValue(framingVM, decS);

                // Camera
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
                // Use UI controls when available (triggers proper recalculation)
                var mainWindow = Application.Current.MainWindow;
                var rotSlider = FindRotationSlider(mainWindow);
                if (rotSlider != null) rotSlider.Value = 0;
                else vmType.GetProperty("Rotation")?.SetValue(framingVM, 0.0);

                var hCtrl = FindTextBoxNearLabel(mainWindow, "Horizontal panels");
                if (hCtrl != null) { hCtrl.Text = "1"; hCtrl.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); }
                else vmType.GetProperty("HorizontalPanels")?.SetValue(framingVM, 1);

                var vCtrl = FindTextBoxNearLabel(mainWindow, "Vertical panels");
                if (vCtrl != null) { vCtrl.Text = "1"; vCtrl.GetBindingExpression(TextBox.TextProperty)?.UpdateSource(); }
                else vmType.GetProperty("VerticalPanels")?.SetValue(framingVM, 1);

                // Load image (rotation, panels, overlap applied after image loads)
                var loadCmd = vmType.GetProperty("LoadImageCommand")?.GetValue(framingVM);
                if (loadCmd != null)
                {
                    var execMethod = loadCmd.GetType().GetMethod("Execute", new[] { typeof(object) });
                    execMethod?.Invoke(loadCmd, new object[] { null });
                }

                // Reapply rotation + panels after image loads (LoadImage resets them)
                _ = ReapplyAfterImageLoad(framingVM, target);
            }
            catch { }
        }

        private async Task ReapplyAfterImageLoad(object framingVM, ProjectTarget target)
        {
            // Poll until the panel UI controls are available (image loaded & visual tree rendered)
            // NINA hides the mosaic section while downloading the sky survey image
            TextBox hPanel = null, vPanel = null;
            for (int i = 0; i < 30; i++) // up to 30 seconds
            {
                await Task.Delay(1000);
                bool found = false;
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    var mw = Application.Current.MainWindow;
                    hPanel = FindTextBoxNearLabel(mw, "Horizontal panels");
                    vPanel = FindTextBoxNearLabel(mw, "Vertical panels");
                    found = hPanel != null && vPanel != null;
                });
                if (found) break;
            }

            // Wait for NINA's post-load recalculations to settle
            // Controls appear before recalc finishes, so we need extra time
            await Task.Delay(3000);

            // Apply all values, then re-apply panels at the end
            // (overlap nudge can trigger recalc that resets panels)
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var mainWindow = Application.Current.MainWindow;

                    // Rotation via slider
                    var rotationSlider = FindRotationSlider(mainWindow);
                    if (rotationSlider != null)
                        rotationSlider.Value = target.RotationDeg;

                    // Panels first pass
                    var hCtrl = FindTextBoxNearLabel(mainWindow, "Horizontal panels");
                    var vCtrl = FindTextBoxNearLabel(mainWindow, "Vertical panels");
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
                    var overlapControl = FindOverlapControl(mainWindow);
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

                    // Final overlap value
                    var overlapControl = FindOverlapControl(mainWindow);
                    if (overlapControl != null)
                    {
                        double overlap = target.PanelOverlapPercent > 0 ? target.PanelOverlapPercent : 20.0;
                        overlapControl.Text = overlap.ToString("F1");
                        overlapControl.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                    }

                    // Re-apply panels to ensure they stick after overlap recalc
                    var hCtrl = FindTextBoxNearLabel(mainWindow, "Horizontal panels");
                    var vCtrl = FindTextBoxNearLabel(mainWindow, "Vertical panels");
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

        internal static TextBox FindOverlapControl(DependencyObject root)
        {
            // Find the TextBox near "Panel overlap" label
            return FindTextBoxNearLabel(root, "Panel overlap");
        }

        internal static Slider FindRotationSlider(DependencyObject root)
        {
            // Find the Slider near "Rotation" label
            var label = FindVisualChild(root, e =>
                e is TextBlock tb && tb.Text == "Rotation") as TextBlock;

            if (label == null) return null;

            var parent = VisualTreeHelper.GetParent(label) as FrameworkElement;
            for (int i = 0; i < 5; i++)
            {
                if (parent == null) break;
                var slider = FindVisualChild(parent, e => e is Slider) as Slider;
                if (slider != null) return slider;
                parent = VisualTreeHelper.GetParent(parent) as FrameworkElement;
            }
            return null;
        }

        internal static TextBox FindTextBoxNearLabel(DependencyObject root, string labelText)
        {
            // Find the label first
            var label = FindVisualChild(root, e =>
                e is TextBlock tb && tb.Text == labelText) as TextBlock;

            if (label == null) return null;

            // Walk up to the parent container
            var parent = VisualTreeHelper.GetParent(label) as FrameworkElement;
            for (int i = 0; i < 5; i++)
            {
                if (parent == null) break;
                // Look for a TextBox sibling in this container
                var textBox = FindVisualChild(parent, e => e is TextBox) as TextBox;
                if (textBox != null) return textBox;
                parent = VisualTreeHelper.GetParent(parent) as FrameworkElement;
            }
            return null;
        }

        private object FindFramingAssistantVM()
        {
            try
            {
                var mainDC = Application.Current?.MainWindow?.DataContext;
                if (mainDC == null) return null;
                return mainDC.GetType().GetProperty("FramingAssistantVM")?.GetValue(mainDC);
            }
            catch { return null; }
        }

        // ── Visual tree helpers ──

        private static DependencyObject FindVisualChild(DependencyObject parent, Func<DependencyObject, bool> predicate)
        {
            if (parent == null) return null;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (predicate(child)) return child;

                var found = FindVisualChild(child, predicate);
                if (found != null) return found;
            }
            return null;
        }

        internal static void FindAllTextBlocksContaining(DependencyObject parent, string substring, List<string> results, int depth = 0)
        {
            if (parent == null || depth > 30) return;
            if (parent is TextBlock tb && tb.Text != null && tb.Text.IndexOf(substring, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                results.Add($"    TextBlock: \"{tb.Text}\"");
            }
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                FindAllTextBlocksContaining(VisualTreeHelper.GetChild(parent, i), substring, results, depth + 1);
            }
        }

        private static bool ContainsTextBlock(DependencyObject parent, string text)
        {
            if (parent is TextBlock tb && tb.Text == text) return true;
            var found = FindVisualChild(parent, e => e is TextBlock t && t.Text == text);
            return found != null;
        }

        private static void DumpVisualTree(DependencyObject parent, int depth, System.Text.StringBuilder sb, int maxDepth)
        {
            if (parent == null || depth > maxDepth) return;

            string indent = new string(' ', depth * 2);
            string typeName = parent.GetType().Name;
            string extra = "";

            if (parent is TextBlock tb && !string.IsNullOrEmpty(tb.Text))
                extra = $" Text=\"{tb.Text.Substring(0, Math.Min(tb.Text.Length, 50))}\"";
            else if (parent is ContentControl cc && cc.Content is string s)
                extra = $" Content=\"{s}\"";
            else if (parent is FrameworkElement fe && !string.IsNullOrEmpty(fe.Name))
                extra = $" Name=\"{fe.Name}\"";

            sb.AppendLine($"{indent}{typeName}{extra}");

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                DumpVisualTree(VisualTreeHelper.GetChild(parent, i), depth + 1, sb, maxDepth);
            }
        }
    }
}
