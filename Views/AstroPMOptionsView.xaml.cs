using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;
using AstroPM.NINA.Plugin.ViewModels;

namespace AstroPM.NINA.Plugin.Views
{
    public partial class AstroPMOptionsView : UserControl
    {
        public AstroPMOptionsView()
        {
            InitializeComponent();
            DataContext = new AstroPMOptionsViewModel();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var (lat, lon) = GetObservatoryLocation();
            SimulatorPanelControl.Initialize(lat, lon);
        }

        private static (double lat, double lon) GetObservatoryLocation()
        {
            try
            {
                var ps = AstroPMPlugin.ProfileService;
                if (ps?.ActiveProfile?.AstrometrySettings == null) return (0, 0);
                return (ps.ActiveProfile.AstrometrySettings.Latitude,
                        ps.ActiveProfile.AstrometrySettings.Longitude);
            }
            catch { return (0, 0); }
        }

        private void OpenAstroPMWebsite(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("https://www.astro-pm.com") { UseShellExecute = true });
            }
            catch { }
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
                e.Handled = true;
            }
            catch { }
        }

        /// <summary>
        /// Make the mouse wheel scroll the whole plugin page everywhere, so it never
        /// "sticks" over child controls (target cards, the log list, ComboBoxes, the
        /// inner browse lists). A nested scrollable region under the pointer still gets
        /// to scroll itself first — only once it hits its own top/bottom does the page
        /// take over.
        /// </summary>
        private void RootScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;
            var outer = (ScrollViewer)sender;

            var node = e.OriginalSource as DependencyObject;
            while (node != null && node != outer)
            {
                if (node is ScrollViewer inner && inner != outer && inner.ScrollableHeight > 0)
                {
                    bool canUp = e.Delta > 0 && inner.VerticalOffset > 0.5;
                    bool canDown = e.Delta < 0 && inner.VerticalOffset < inner.ScrollableHeight - 0.5;
                    if (canUp || canDown) return; // let the nested region scroll
                }
                node = GetVisualOrLogicalParent(node);
            }

            outer.ScrollToVerticalOffset(outer.VerticalOffset - e.Delta);
            e.Handled = true;
        }

        private static DependencyObject GetVisualOrLogicalParent(DependencyObject node)
        {
            DependencyObject parent = (node is Visual || node is System.Windows.Media.Media3D.Visual3D)
                ? VisualTreeHelper.GetParent(node) : null;
            return parent ?? LogicalTreeHelper.GetParent(node);
        }
    }
}
