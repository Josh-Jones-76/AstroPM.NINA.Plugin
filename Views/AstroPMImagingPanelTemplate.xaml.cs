using AstroPM.NINA.Plugin.ViewModels;
using System.ComponentModel.Composition;
using System.Windows;

namespace AstroPM.NINA.Plugin.Views {

    [Export(typeof(ResourceDictionary))]
    public partial class AstroPMImagingPanelTemplate : ResourceDictionary {
        public AstroPMImagingPanelTemplate() {
            InitializeComponent();
        }

        private void Chart_Loaded(object sender, RoutedEventArgs e) {
            if (!(sender is NightlyChartControl chart)) return;

            global::NINA.Core.Utility.Logger.Info($"AstroPM Panel | Chart_Loaded fired, DataContext type: {chart.DataContext?.GetType().Name ?? "null"}");

            if (!(chart.DataContext is AstroPMImagingPanelVM vm)) {
                // DataContext may not be set yet — listen for it
                chart.DataContextChanged += (s, args) => {
                    global::NINA.Core.Utility.Logger.Info($"AstroPM Panel | DataContextChanged to: {args.NewValue?.GetType().Name ?? "null"}");
                    if (args.NewValue is AstroPMImagingPanelVM newVm) {
                        WireUpChart(chart, newVm);
                    }
                };
                return;
            }

            WireUpChart(chart, vm);
        }

        private static void WireUpChart(NightlyChartControl chart, AstroPMImagingPanelVM vm) {
            global::NINA.Core.Utility.Logger.Info("AstroPM Panel | Wiring up chart to VM");

            vm.PushChartData = (slots, profiles, log) => {
                chart.Dispatcher.Invoke(() => {
                    global::NINA.Core.Utility.Logger.Info($"AstroPM Panel | PushChartData called: {log?.Count ?? 0} log entries");
                    chart.Visibility = Visibility.Visible;
                    chart.SetData(slots, profiles, log);
                });
            };

            if (vm.HasChartData && vm.ChartSlots != null) {
                global::NINA.Core.Utility.Logger.Info("AstroPM Panel | Chart data already available, pushing immediately");
                chart.Visibility = Visibility.Visible;
                chart.SetData(vm.ChartSlots, vm.ChartProfiles, vm.ChartLog);
            }
        }
    }
}
