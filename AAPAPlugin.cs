using System.ComponentModel.Composition;
using System.Threading.Tasks;
using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;

namespace NINA.Plugins.AAPA {

    /// <summary>
    /// NINA Plugin Manifest for the AAPA Controller plugin.
    /// PluginBase reads plugin metadata (Name, Author, Version, etc.)
    /// directly from the AssemblyInfo.cs assembly-level attributes.
    /// </summary>
    [Export(typeof(IPluginManifest))]
    public class AAPAPlugin : PluginBase {

        public AAPAPlugin() {
            // Upgrade persisted settings when a new plugin version is installed
            if (Properties.Settings.Default.UpdateSettings) {
                Properties.Settings.Default.Upgrade();
                Properties.Settings.Default.UpdateSettings = false;
                CoreUtil.SaveSettings(Properties.Settings.Default);
            }
        }

        /// <summary>Shared AAPA hardware controller (singleton for the plugin lifetime).</summary>
        public static AAPAControllerService ControllerService { get; } = new AAPAControllerService();

        public override Task Teardown() {
            try {
                ControllerService?.Dispose();
            } catch { }
            return base.Teardown();
        }
    }
}
