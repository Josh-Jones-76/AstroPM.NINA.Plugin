using AstroPM.NINA.Plugin.Instructions;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;

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
                chart.SetData(instruction.LastSlots, instruction.LastProfiles, instruction.LastLog);
            }
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
