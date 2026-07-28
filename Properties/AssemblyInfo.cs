using System.Reflection;
using System.Runtime.InteropServices;

// ── Required by NINA Plugin Loader ──────────────────────────────────────────
// PluginBase reads these attributes to populate the plugin manifest.

// Plugin name (displayed in NINA Plugin Manager)
[assembly: AssemblyTitle("AAPA Controller")]

// Unique GUID — MUST NEVER CHANGE once published
[assembly: Guid("a3c8f4e2-7b1d-4a9e-b5f0-1c2d3e4f5a6b")]

// Plugin version (Major.Minor.Patch.Build)
[assembly: AssemblyVersion("5.0.0.0")]
[assembly: AssemblyFileVersion("5.0.0.0")]

// Short description (REQUIRED by NINA)
[assembly: AssemblyMetadata("ShortDescription",
    "Integrates AAPA hardware with TPPA for automated polar alignment via stepper motor control.")]

// Author
[assembly: AssemblyCompany("Blayzer")]
[assembly: AssemblyProduct("NINA AAPA Plugin")]
[assembly: AssemblyCopyright("Copyright © 2025")]

// ── Recommended metadata ────────────────────────────────────────────────────

[assembly: AssemblyMetadata("MinimumApplicationVersion", "3.0.0.2017")]

[assembly: AssemblyMetadata("License", "MIT")]
[assembly: AssemblyMetadata("LicenseURL", "https://opensource.org/licenses/MIT")]

[assembly: AssemblyMetadata("Repository", "https://github.com/Blayzer-Astro/AAPA-Controller-Plugin")]
[assembly: AssemblyMetadata("Homepage", "https://github.com/Blayzer-Astro/AAPA-Controller-Plugin")]

[assembly: AssemblyMetadata("LongDescription",
    "Please read the Readme on my Github.")]

[assembly: AssemblyMetadata("Tags", "Polar Alignment, AAPA, TPPA, Stepper Motor, Automated")]

// ── Other ───────────────────────────────────────────────────────────────────
[assembly: ComVisible(false)]
[assembly: AssemblyConfiguration("")]
[assembly: AssemblyTrademark("")]
[assembly: AssemblyCulture("")]
