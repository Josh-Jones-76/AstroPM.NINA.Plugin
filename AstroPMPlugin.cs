using System;
using System.Reflection;
using System.ComponentModel.Composition;
using System.Threading.Tasks;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;

[assembly: AssemblyTitle("Astro PM")]
[assembly: AssemblyDescription("Astro PM connects your cloud-hosted imaging projects to NINA. Set up your sync token to link your account, browse and load targets directly into the Framing Assistant for manual session planning, or use the built-in Simulator to generate a fully automated nightly schedule.")]
[assembly: AssemblyCompany("Astro PM")]
[assembly: AssemblyProduct("AstroPM.NINA.Plugin")]
[assembly: AssemblyCopyright("Copyright © Astro PM 2026")]
[assembly: AssemblyVersion("1.0.2.0")]
[assembly: AssemblyFileVersion("1.0.2.0")]
[assembly: System.Runtime.InteropServices.Guid("C8F1A2B3-D4E5-6F78-9A0B-1C2D3E4F5A6B")]
[assembly: System.Runtime.InteropServices.ComVisible(false)]

[assembly: AssemblyMetadata("Identifier", "C8F1A2B3-D4E5-6F78-9A0B-1C2D3E4F5A6B")]
[assembly: AssemblyMetadata("Name", "Astro PM")]
[assembly: AssemblyMetadata("Author", "Astro PM")]
[assembly: AssemblyMetadata("Description", "Astro PM connects your cloud-hosted imaging projects to NINA. Set up your sync token to link your account, browse and load targets directly into the Framing Assistant for manual session planning, or use the built-in Simulator to generate a fully automated nightly schedule.")]
[assembly: AssemblyMetadata("License", "MIT")]
[assembly: AssemblyMetadata("LicenseURL", "https://opensource.org/licenses/MIT")]
[assembly: AssemblyMetadata("Homepage", "https://astro-pm.com")]
[assembly: AssemblyMetadata("Repository", "https://github.com/Josh-Jones-76/AstroPM.NINA.Plugin")]
[assembly: AssemblyMetadata("Tags", "Sync,Targets,Framing,StarLog,Cloud")]
[assembly: AssemblyMetadata("FeaturedImageURL", "https://astro-pm.com/downloads/AstroPM-Logo-Icon-500x500.png")]
[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.0.0.9001")]

namespace AstroPM.NINA.Plugin
{
    [Export(typeof(IPluginManifest))]
    public class AstroPMPlugin : PluginBase
    {
        private FramingInjector _framingInjector;

        public static IProfileService ProfileService { get; private set; }

        [ImportingConstructor]
        public AstroPMPlugin(IProfileService profileService)
        {
            ProfileService = profileService;
        }

        public override Task Initialize()
        {
            try
            {
                _framingInjector = new FramingInjector();
                _framingInjector.Start();
            }
            catch { }
            return Task.CompletedTask;
        }

        public override Task Teardown()
        {
            _framingInjector?.Stop();
            return Task.CompletedTask;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Simple RelayCommand
    // ════════════════════════════════════════════════════════════════════

    public class RelayCommand : System.Windows.Input.ICommand
    {
        private readonly Func<object, Task> _executeAsync;
        private readonly Predicate<object> _canExecute;

        public RelayCommand(Func<object, Task> executeAsync, Predicate<object> canExecute = null)
        {
            _executeAsync = executeAsync;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter) => _canExecute?.Invoke(parameter) ?? true;

        public async void Execute(object parameter) => await _executeAsync(parameter);

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
