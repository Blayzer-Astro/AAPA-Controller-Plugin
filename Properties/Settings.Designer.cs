// Auto-generated — do not edit manually.
// Regenerate with: right-click Settings.settings → Run Custom Tool
using System.Configuration;
using System.Runtime.CompilerServices;

namespace NINA.Plugins.AAPA.Properties {

    [global::System.Runtime.CompilerServices.CompilerGeneratedAttribute()]
    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("Microsoft.VisualStudio.Editors.SettingsDesigner.SettingsSingleFileGenerator", "17.0.3.0")]
    internal sealed partial class Settings : global::System.Configuration.ApplicationSettingsBase {

        private static Settings defaultInstance = (Settings)global::System.Configuration.ApplicationSettingsBase.Synchronized(new Settings());

        public static Settings Default => defaultInstance;

        // ── Upgrade ───────────────────────────────────────────────────────────
        [UserScopedSetting, DefaultSettingValue("True")]
        public bool UpdateSettings {
            get => (bool)this["UpdateSettings"];
            set { this["UpdateSettings"] = value; }
        }

        // ── Motor geometry ────────────────────────────────────────────────────
        [UserScopedSetting, DefaultSettingValue("200")]
        public int StepsPerRevolution {
            get => (int)this["StepsPerRevolution"];
            set { this["StepsPerRevolution"] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("16")]
        public int AzimuthMicrosteps {
            get => (int)this["AzimuthMicrosteps"];
            set { this["AzimuthMicrosteps"] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("16")]
        public int AltitudeMicrosteps {
            get => (int)this["AltitudeMicrosteps"];
            set { this["AltitudeMicrosteps"] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("1")]
        public double AzimuthGearRatio {
            get => (double)this["AzimuthGearRatio"];
            set { this["AzimuthGearRatio"] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("1")]
        public double AltitudeGearRatio {
            get => (double)this["AltitudeGearRatio"];
            set { this["AltitudeGearRatio"] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("False")]
        public bool ReverseAzimuth {
            get => (bool)this["ReverseAzimuth"];
            set { this["ReverseAzimuth"] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("False")]
        public bool ReverseAltitude {
            get => (bool)this["ReverseAltitude"];
            set { this["ReverseAltitude"] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("0")]
        public int AzimuthBacklash {
            get => (int)this["AzimuthBacklash"];
            set { this["AzimuthBacklash"] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("0")]
        public int AltitudeBacklash {
            get => (int)this["AltitudeBacklash"];
            set { this["AltitudeBacklash"] = value; }
        }

        // ── Auto-Pilot ────────────────────────────────────────────────────────
        [UserScopedSetting, DefaultSettingValue("0.01")]
        public double ToleranceDegrees {
            get => (double)this["ToleranceDegrees"];
            set { this["ToleranceDegrees"] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("3")]
        public double SettleTimeSeconds {
            get => (double)this["SettleTimeSeconds"];
            set { this["SettleTimeSeconds"] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("20")]
        public int MaxIterations {
            get => (int)this["MaxIterations"];
            set { this["MaxIterations"] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("0.5")]
        public double MaxCorrectionDegrees {
            get => (double)this["MaxCorrectionDegrees"];
            set { this["MaxCorrectionDegrees"] = value; }
        }

        // ── AAPA speed / accel ────────────────────────────────────────────────
        [UserScopedSetting, DefaultSettingValue("1500")]
        public int AzimuthSpeed {
            get => (int)this["AzimuthSpeed"];
            set { this["AzimuthSpeed"] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("1500")]
        public int AltitudeSpeed {
            get => (int)this["AltitudeSpeed"];
            set { this["AltitudeSpeed"] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("500")]
        public int AzimuthAccel {
            get => (int)this["AzimuthAccel"];
            set { this["AzimuthAccel"] = value; }
        }

        [UserScopedSetting, DefaultSettingValue("500")]
        public int AltitudeAccel {
            get => (int)this["AltitudeAccel"];
            set { this["AltitudeAccel"] = value; }
        }
    }
}
