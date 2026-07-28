using System.ComponentModel.Composition;
using System.Windows;

namespace NINA.Plugins.AAPA.Instructions {
    [Export(typeof(ResourceDictionary))]
    public partial class AAPAInstructionTemplates : ResourceDictionary {
        public AAPAInstructionTemplates() {
            InitializeComponent();
        }
    }
}
