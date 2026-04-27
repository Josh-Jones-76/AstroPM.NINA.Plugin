using System.ComponentModel.Composition;
using System.Windows;

namespace AstroPM.NINA.Plugin.Views
{
    [Export(typeof(ResourceDictionary))]
    public partial class AstroPMOptionsTemplate : ResourceDictionary
    {
        public AstroPMOptionsTemplate()
        {
            InitializeComponent();
        }
    }
}
