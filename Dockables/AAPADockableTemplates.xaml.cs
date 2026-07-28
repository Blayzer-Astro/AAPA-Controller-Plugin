using System.ComponentModel.Composition;
using System.Windows;

namespace NINA.Plugins.AAPA.Dockables {
    [Export(typeof(ResourceDictionary))]
    public partial class AAPADockableTemplates : ResourceDictionary {
        public AAPADockableTemplates() {
            InitializeComponent();
        }
    }
}
