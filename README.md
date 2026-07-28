# NINA AAPA Plugin

A NINA plugin that connects the **AAPA** (Astrophilos Automated Polar Alignment) device to the **TPPA** (Three Point Polar Alignment) plugin.

## Overview

| | |
|--|--|
| **Hardware** | AAPA — FYSETC E4 (ESP32) with 2 stepper motors (Azimuth + Altitude) |
| **Software** | NINA 3.x + TPPA Plugin by isbeorn |
| **Communication** | Serial (USB/COM), 115200 Baud |

This plugin replaces the standalone `platedual.py` / `platedual.exe` tool and integrates AAPA control directly into NINA.

## Features

- 🔌 **Auto-Connection** — Automatically detects the AAPA on all COM ports
- 📡 **TPPA Integration** — Reads polar alignment errors from NINA logs in real-time
- 🤖 **Auto-Pilot** — Calculates and automatically sends correction movements until within tolerance
- 🕹️ **Manual Control** — Azimuth/Altitude click controls with configurable step sizes
- 🏠 **Homing** — StallGuard homing for the Altitude axis (Y)
- 📋 **Sequence Instruction** — "AAPA Auto-Align" for fully automated sequences
- ⚙️ **Configurable** — Gear ratio, microstepping, backlash, speed/accel

## Installation

### Requirements
- NINA 3.0+
- TPPA Plugin (by isbeorn, from the NINA plugin manager)
- .NET 8 SDK (for compiling)
- Visual Studio 2022 or `dotnet build`

### Compiling & Installing

```powershell
cd nina.plugin.aapa

# Debug build
dotnet build

# Release build (automatically copies the DLL to %LOCALAPPDATA%\NINA\Plugins\)
dotnet build -c Release
```

Start NINA → Plugins → AAPA Controller will appear in the plugin list.

### Manual Installation
Copy the `NINA.Plugin.AAPA.dll` to the following directory:
```
%LOCALAPPDATA%\NINA\Plugins\NINA.Plugin.AAPA\
```

## Usage

### 1. Connecting

In the dockable panel:
1. Select the COM port (or "Auto" for automatic detection)
2. Click **Connect**
3. LED turns green → connected

### 2. Polar Alignment with Auto-Pilot

1. Start TPPA measurement (click "Measure" in the TPPA plugin)
2. In the AAPA panel: click **▶ Start Auto-Pilot**
3. The Auto-Pilot reads the TPPA errors and sends corrections until within tolerance

### 3. Automation (Sequence)

1. Add the sequence instruction "**AAPA Auto-Align**" after a TPPA step
2. Configure tolerance, max iterations, and settle time
3. Start the sequence → fully automated!

## Configuration

| Parameter | Default | Description |
|-----------|---------|--------------|
| Steps/Rev | 200 | Full steps per motor revolution (NEMA 17 = 200) |
| Microsteps | 16 | Microstep mode (as configured in the AAPA firmware) |
| Gear Ratio | 1.0 | Gear ratio from motor to telescope axis |
| Tolerance | 0.01° | Auto-Pilot stops when the error is below this value |
| Settle Time | 3 s | Wait time after each movement before the next measurement |
| Max Iterations | 20 | Maximum correction steps (0 = unlimited) |
| Max Correction | 0.5° | Maximum correction per iteration |

## AAPA Serial Protocol

| Command | Function |
|--------|---------|
| `X<n>` | Move Azimuth N steps |
| `Y<n>` | Move Altitude N steps |
| `:STATUS` | Get status (Pos, Busy, Homed) |
| `:STOP` | Emergency stop |
| `:HOMEY` | Home Y-axis (StallGuard) |
| `:SPDX <n>` | Azimuth speed (step/s) |
| `:SPDY <n>` | Altitude speed (step/s) |
| `:ACCX <n>` | Azimuth acceleration (step/s²) |
| `:ACCY <n>` | Altitude acceleration (step/s²) |
| `:SAVE` | Save configuration to flash |

## Step Calculation

```
steps = round((degrees / 360) × steps_per_rev × microsteps × gear_ratio)
```

Identical to the formula in `platedual.py`.

## Project Structure

```
nina.plugin.aapa/
├── AAPAPlugin.cs                          # Plugin manifest (MEF Export)
├── NINA.Plugin.AAPA.csproj
│
├── AAPA/
│   ├── AAPAControllerService.cs           # Serial communication + auto-discovery
│   └── AAPAStatus.cs                      # Status data model
│
├── Alignment/
│   ├── TPPALogMonitor.cs                  # TPPA log file monitoring
│   ├── AlignmentEngine.cs                 # Error → steps calculation
│   ├── AutoPilotController.cs             # Automatic correction loop
│   └── RatioCalibrationController.cs      # Calculates Gear Ratio from movement
│
├── Dockables/
│   ├── AAPADockable.cs                    # IDockableVM MEF Export
│   ├── AAPADockableVM.cs                  # ViewModel
│   ├── AAPADockableTemplates.xaml         # UI
│   └── AAPADockableTemplates.xaml.cs
│
├── Instructions/
│   ├── AAPAAlignInstruction.cs            # Sequence Instruction
│   ├── AAPAInstructionTemplates.xaml      # Instruction UI
│   └── AAPAInstructionTemplates.xaml.cs
│
└── Properties/
    ├── AssemblyInfo.cs
    ├── Settings.Designer.cs               # Typed settings
    └── Settings.settings
```

## License

MIT — Based on the NINA Plugin Template by isbeorn.

## Links

- [AAPA Hardware](https://astrophiloslab.com/aapa)
- [AAPA GitHub](https://github.com/AstrophilosLab/AAPA)
- [TPPA Plugin](https://github.com/isbeorn/nina.plugin.polaralignment)
- [NINA Plugin Template](https://github.com/isbeorn/nina.plugin.template)
