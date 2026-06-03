using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
    }
}
