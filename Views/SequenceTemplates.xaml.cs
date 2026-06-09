using AstroPM.NINA.Plugin.Instructions;
using AstroPM.NINA.Plugin.Models;
using AstroPM.NINA.Plugin.ViewModels;
using System;
using System.Collections.Generic;
using System.Linq;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AstroPM.NINA.Plugin.Views {

    [Export(typeof(ResourceDictionary))]
    public partial class SequenceTemplates : ResourceDictionary {
        public SequenceTemplates() {
            InitializeComponent();
        }

        private void NightlyChart_Loaded(object sender, RoutedEventArgs e) {
            if (sender is NightlyChartControl chart && chart.Tag is TargetInstructionSet instruction) {
                UpdateChart(chart, instruction);
                instruction.PropertyChanged += (s, args) => {
                    if (args.PropertyName == nameof(TargetInstructionSet.HasChartData))
                        chart.Dispatcher.Invoke(() => UpdateChart(chart, instruction));
                };
            }
        }

        private static void UpdateChart(NightlyChartControl chart, TargetInstructionSet instruction) {
            if (instruction.HasChartData) {
                chart.Visibility = Visibility.Visible;
                var profiles = instruction.LastProfiles;
                var colorMap = BuildProfileColorMap(profiles, instruction.LastLog);
                chart.SetData(instruction.LastSlots, profiles, instruction.LastLog,
                    idx => colorMap.TryGetValue(idx, out var c) ? c : SimulatorViewModel.TargetCurveColors[idx % SimulatorViewModel.TargetCurveColors.Length]);
            }
        }

        private static Dictionary<int, Color> BuildProfileColorMap(List<TargetProfile> profiles, List<SimLogEntry> log) {
            var firstSlewTime = new Dictionary<int, DateTime>();
            if (log != null) {
                foreach (var entry in log.Where(e => e.Command == "Slew")) {
                    int pIdx = profiles.FindIndex(p => p.DisplayName == entry.Target);
                    if (pIdx >= 0 && !firstSlewTime.ContainsKey(pIdx))
                        firstSlewTime[pIdx] = entry.UtcTime;
                }
            }
            var orderedIndices = Enumerable.Range(0, profiles.Count)
                .OrderBy(i => firstSlewTime.ContainsKey(i) ? firstSlewTime[i] : DateTime.MaxValue)
                .ToList();

            var map = new Dictionary<int, Color>();
            var projectColorMap = new Dictionary<string, Color>();
            int colorIdx = 0;
            for (int oi = 0; oi < orderedIndices.Count; oi++) {
                int i = orderedIndices[oi];
                string projectName = profiles[i].Target.TargetName;
                if (!projectColorMap.TryGetValue(projectName, out var color)) {
                    color = SimulatorViewModel.TargetCurveColors[colorIdx % SimulatorViewModel.TargetCurveColors.Length];
                    projectColorMap[projectName] = color;
                    colorIdx++;
                }
                map[i] = color;
            }
            return map;
        }

        private void LogList_Loaded(object sender, RoutedEventArgs e) {
            if (!(sender is ListView listView)) return;
            if (!(listView.DataContext is TargetInstructionSet instruction)) return;

            instruction.PropertyChanged += (s, args) => {
                if (args.PropertyName == nameof(TargetInstructionSet.ActiveLogEntry)) {
                    listView.Dispatcher.Invoke(() => {
                        if (!instruction.AutoScrollLog) return;
                        var entry = instruction.ActiveLogEntry;
                        if (entry == null || !listView.Items.Contains(entry)) return;

                        listView.SelectedItem = entry;
                        listView.ScrollIntoView(entry);

                        // Center the active item by scrolling a few items ahead into view,
                        // then scrolling back to the target item.
                        int idx = listView.Items.IndexOf(entry);
                        int ahead = Math.Min(idx + 6, listView.Items.Count - 1);
                        listView.ScrollIntoView(listView.Items[ahead]);
                        listView.ScrollIntoView(entry);
                    });
                }
            };
        }
    }
}
