using AstroPM.NINA.Plugin.ViewModels;
using AstroPM.NINA.Plugin.Models;
using System;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;

namespace AstroPM.NINA.Plugin.Views {

    [Export(typeof(ResourceDictionary))]
    public partial class AstroPMLogPanelTemplate : ResourceDictionary {
        public AstroPMLogPanelTemplate() {
            InitializeComponent();
        }

        private void LogList_Loaded(object sender, RoutedEventArgs e) {
            if (!(sender is ListView listView)) return;

            void WireUp(AstroPMLogPanelVM vm) {
                vm.PropertyChanged += (s, args) => {
                    if (args.PropertyName != nameof(AstroPMLogPanelVM.ActiveLogEntry)) return;
                    var entry = vm.ActiveLogEntry;
                    if (entry == null || !vm.AutoScrollLog) return;

                    listView.Dispatcher.Invoke(() => {
                        listView.SelectedItem = entry;
                        int idx = listView.Items.IndexOf(entry);
                        if (idx < 0) return;
                        int ahead = Math.Min(idx + 6, listView.Items.Count - 1);
                        listView.ScrollIntoView(listView.Items[ahead]);
                        listView.ScrollIntoView(entry);
                    });
                };
            }

            if (listView.DataContext is AstroPMLogPanelVM currentVm) {
                WireUp(currentVm);
            } else {
                listView.DataContextChanged += (s, args) => {
                    if (args.NewValue is AstroPMLogPanelVM newVm)
                        WireUp(newVm);
                };
            }
        }
    }
}
