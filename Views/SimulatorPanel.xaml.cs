using AstroPM.NINA.Plugin.Models;
using AstroPM.NINA.Plugin.ViewModels;
using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AstroPM.NINA.Plugin.Views {

    public partial class SimulatorPanel : UserControl {

        private SimulatorViewModel _vm;

        public SimulatorPanel() {
            InitializeComponent();
        }

        public void Initialize(double latitude, double longitude) {
            _vm = new SimulatorViewModel();
            _vm.SetObservatoryLocation(latitude, longitude);
            DataContext = _vm;

            _vm.ChartDataReady += OnChartDataReady;
            _vm.ScrubberPositionChanged += OnScrubberPositionChanged;
            SimChart.ScrubberMoved += OnChartScrubberMoved;
            SimChart.NowCrossedInstruction += OnNowCrossedInstruction;
        }

        private void OnChartDataReady() {
            if (_vm.Slots == null || _vm.Slots.Count == 0 || _vm.Profiles == null || _vm.Log == null) return;
            SimChart.SetData(_vm.Slots, _vm.Profiles, _vm.Log, _vm.GetProfileColor);

            var slots = _vm.Slots;
            var utcNow = DateTime.UtcNow;
            var graphStart = slots[0].UtcStart;
            var graphEnd = slots[slots.Count - 1].UtcStart.AddSeconds(300);

            DateTime scrubUtc;
            if (utcNow >= graphStart && utcNow <= graphEnd)
                scrubUtc = utcNow;
            else
                scrubUtc = _vm.Log.FirstOrDefault(e => e.UtcTime != default)?.UtcTime ?? graphStart;

            double frac = (scrubUtc - graphStart).TotalSeconds / (graphEnd - graphStart).TotalSeconds;
            SimChart.SetScrubberPosition(Math.Clamp(frac, 0, 1));
            _vm.SelectLogEntryAtTime(scrubUtc);
            _vm.UpdateCardsAtTime(scrubUtc);
        }

        private void OnScrubberPositionChanged(SimLogEntry entry) {
            if (entry?.UtcTime != default && _vm.Slots != null && _vm.Slots.Count > 0) {
                double frac = (_vm.Slots.Count > 0)
                    ? (entry.UtcTime - _vm.Slots[0].UtcStart).TotalSeconds /
                      ((_vm.Slots[_vm.Slots.Count - 1].UtcStart.AddSeconds(300) - _vm.Slots[0].UtcStart).TotalSeconds)
                    : 0;
                SimChart.SetScrubberPosition(Math.Clamp(frac, 0, 1));
                _vm.UpdateCardsAtTime(entry.UtcTime);
            }

            if (entry != null && SimLogList.Items.Contains(entry))
                SimLogList.ScrollIntoView(entry);
        }

        private void OnChartScrubberMoved(DateTime utc) {
            _vm.SelectLogEntryAtTime(utc);
            _vm.UpdateCardsAtTime(utc);
        }

        private void SimPanelTab_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) {
            if (!(sender is FrameworkElement fe)) return;
            if (!(fe.DataContext is PanelTabModel clickedTab)) return;

            FrameworkElement parent = fe;
            TargetCardModel card = null;
            while (parent != null) {
                parent = VisualTreeHelper.GetParent(parent) as FrameworkElement;
                if (parent?.DataContext is TargetCardModel tc) { card = tc; break; }
            }
            if (card == null) return;

            _vm?.SwitchPanel(card.ProfileIndex, clickedTab.PanelIndex);
        }

        private void OnNowCrossedInstruction(DateTime utc) {
            _vm.SelectLogEntryAtTime(utc);
            _vm.UpdateCardsAtTime(utc);
        }

        private void SimLogList_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            if (SimLogList.SelectedItem != null)
                SimLogList.ScrollIntoView(SimLogList.SelectedItem);
        }

        private void SortChip_MouseMove(object sender, MouseEventArgs e) {
            if (e.LeftButton != MouseButtonState.Pressed || !(sender is Border border)) return;
            if (!(border.DataContext is SortChipItem item)) return;
            DragDrop.DoDragDrop(border, new DataObject(typeof(SortChipItem), item), DragDropEffects.Move);
        }

        private void SortChip_DragEnter(object sender, DragEventArgs e) {
            e.Effects = e.Data.GetDataPresent(typeof(SortChipItem)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void SortChip_DragOver(object sender, DragEventArgs e) {
            e.Effects = e.Data.GetDataPresent(typeof(SortChipItem)) ? DragDropEffects.Move : DragDropEffects.None;
            e.Handled = true;
        }

        private void SortChip_Drop(object sender, DragEventArgs e) {
            var dropped = e.Data.GetData(typeof(SortChipItem)) as SortChipItem;
            var target = (sender as Border)?.DataContext as SortChipItem;
            if (dropped == null || target == null || dropped == target) return;
            _vm?.ReorderSortChain(dropped.Criteria, target.Criteria);
        }

        private void BtnCopyLog_Click(object sender, RoutedEventArgs e) {
            if (_vm?.Log == null || _vm.Log.Count == 0) return;

            var sb = new StringBuilder();
            sb.AppendLine("Command\tTime\tTarget\tPanel\tSub\tFilter\tExp\tGain\tOffset\tBin\tRot\tRA\tDEC\tSort1\tSort2\tSort3\tSort4\tAlt\tMoonSep\tMoonOK\tAvoidSep\tDark\tLA\tLASafe");
            foreach (var entry in _vm.Log) {
                sb.AppendLine($"{entry.Command}\t{entry.Time}\t{entry.Target}\t{entry.Panel}\t{entry.SubNum}\t{entry.Filter}\t{entry.Exposure}\t{entry.Gain}\t{entry.Offset}\t{entry.Bin}\t{entry.Rotation}\t{entry.RA}\t{entry.DEC}\t{entry.Sort1}\t{entry.Sort2}\t{entry.Sort3}\t{entry.Sort4}\t{entry.Altitude}\t{entry.MoonSep}\t{entry.MoonSafe}\t{entry.MoonAvoidSep}\t{entry.DarkSafe}\t{entry.LaEnabled}\t{entry.LaSafe}");
            }
            try { Clipboard.SetText(sb.ToString()); } catch { }
        }
    }
}
