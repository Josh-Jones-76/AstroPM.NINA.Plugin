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
